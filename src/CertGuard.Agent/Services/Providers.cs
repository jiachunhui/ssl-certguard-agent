using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CertGuard.Agent.Services;

public interface IDeployProvider
{
    string Name { get; }
    string? LastError { get; }
    /// <summary>返回 (成功, 实际部署的域名列表)</summary>
    Task<(bool ok, string[]? deployedDomains)> DeployAsync(string certPem, string keyPem, string[] domains, CancellationToken ct);
    Task<bool> ReloadAsync(CancellationToken ct);
    bool IsAvailable { get; }
}

public static class Proc
{
    public static async Task<(bool ok, string output)> Exec(string bin, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(bin, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p is null) return (false, "进程启动失败");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode == 0, stdout + stderr);
    }
}

/// <summary>证书文件原子写入：临时文件 + rename，Linux 下统一 600 权限（证书与私钥）</summary>
internal static class CertFile
{
    public static async Task WriteAsync(string path, string content, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, true);
    }

    /// <summary>主域名目录：优先取第一个非通配符条目；纯通配证书则去掉 *. 前缀，
    /// 避免证书目录名出现 '*'（如 /etc/nginx/ssl/*.example.com/）</summary>
    public static string PrimaryDomain(string[] domains)
    {
        var first = domains.FirstOrDefault(d => !string.IsNullOrEmpty(d) && !d.StartsWith("*."));
        if (!string.IsNullOrEmpty(first)) return first;
        if (domains.Length > 0 && domains[0].StartsWith("*.")) return domains[0][2..];
        return domains.Length > 0 && !string.IsNullOrEmpty(domains[0]) ? domains[0] : "unknown";
    }
}

public class NginxProvider : IDeployProvider
{
    public string Name => "nginx";
    public string? LastError { get; private set; }
    public bool IsAvailable =>
        File.Exists("/usr/sbin/nginx") || File.Exists("/usr/bin/nginx") || File.Exists("/usr/local/nginx/sbin/nginx");

    private readonly ILogger<NginxProvider> _log;
    private readonly string _base;
    private string? _nginxBin;
    private bool? _sslModuleOk;

    public NginxProvider(ILogger<NginxProvider> log, string? basePath = null)
    { _log = log; _base = basePath ?? "/etc/nginx/ssl"; }

    /// <summary>
    /// Nginx 证书部署，按域名逐场景处理（与 IIS 语义一致：部分匹配即部署匹配项）：
    /// 1) 无匹配 server 块 → 跳过；全部未匹配 → 失败上报
    /// 2) 匹配块内已配置 ssl_certificate（任何端口）→ 仅替换证书路径
    /// 3) 匹配块仅监听 80 → 基于 80 块创建 443 配置（listen 443 ssl + 证书两行）
    /// 4) 匹配块有证书但非 443 端口 → 同场景 2（替换证书路径）
    /// 边界：listen 带 ssl 但缺 ssl_certificate（可能 include 引入）→ 跳过并警告；
    ///       80 纯重定向块 → 不创建，提示手动配置。
    /// </summary>
    public async Task<(bool ok, string[]? deployedDomains)> DeployAsync(string certPem, string keyPem, string[] domains, CancellationToken ct)
    {
        LastError = null;
        // 主域名目录：优先取第一个非通配符条目；纯通配证书则去掉 *. 前缀，
        // 避免证书目录名出现 '*'（如 /etc/nginx/ssl/*.example.com/）
        var primaryDomain = CertFile.PrimaryDomain(domains);

        // 1. 写入证书（原子写：临时文件 + rename，避免部署中断留下损坏的 PEM；私钥权限 600）
        var dir = Path.Combine(_base, primaryDomain);
        Directory.CreateDirectory(dir);
        var certPath = Path.Combine(dir, "fullchain.pem");
        var keyPath = Path.Combine(dir, "privkey.pem");
        await CertFile.WriteAsync(certPath, certPem, ct);
        await CertFile.WriteAsync(keyPath, keyPem, ct);
        _log.LogInformation("Nginx 证书已写入: {Domain} -> {Dir}", primaryDomain, dir);

        // 2. 解析全部 server 块（include 递归展开 + 注释剥离 + 块级定位）
        var parser = new NginxConfigParser(_log);
        var blocks = await parser.LoadServerBlocksAsync(ct);
        if (blocks.Count == 0)
        {
            LastError = "Nginx 部署失败：未发现任何 server 配置（请确认 nginx.conf 及 include 路径可读）";
            _log.LogError("{Error}", LastError);
            return (false, null);
        }
        _log.LogInformation("Nginx 解析到 {Count} 个 server 块", blocks.Count);

        // 3. 逐域名处理
        var writer = new NginxConfigWriter(_log)
        {
            BackupBase = Path.Combine(_base, ".backup") // 备份目录跟随证书目录，保证可写
        };
        var deployed = new List<string>();
        var skipped = new List<string>();

        foreach (var domain in domains)
        {
            var matched = blocks.Where(b => NginxConfigParser.MatchesDomain(b, domain)).ToList();
            if (matched.Count == 0)
            {
                skipped.Add(domain + " -- Nginx 上无站点服务此域名");
                continue;
            }

            // 实际命中的 server_name：通配符证书条目（*.example.com）展开为具体域名，
            // 平台上报时使用真实站点域名而非通配符本身
            var hitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in matched)
                foreach (var n in b.ServerNames)
                    if (NginxConfigParser.DomainMatch(n, domain))
                        hitNames.Add(n);
            if (hitNames.Count == 0) hitNames.Add(domain); // 兜底（正常情况下不会发生）

            var sslBlocks = matched.Where(b => b.HasSslCertificate).ToList();
            var sslListenOnly = matched.Where(b => !b.HasSslCertificate && (b.HasSslListen || b.HasSslOn)).ToList();
            var httpBlocks = matched.Where(b => !b.HasSslCertificate && !b.HasSslListen && !b.HasSslOn).ToList();

            // 场景 2/4：匹配块内已配置证书（任何端口）→ 全部替换为当前安装证书。
            // 含变量表达式（动态证书，如 $ssl_server_name）的块跳过替换并警告，避免破坏动态证书逻辑。
            var replaceable = new List<NginxServerBlock>();
            var variableCert = new List<NginxServerBlock>();
            foreach (var b in sslBlocks)
            {
                var hasVar = b.SslCertificateLines.Any(v => v.Contains('$'))
                             || b.SslCertificateKeyLines.Any(v => v.Contains('$'));
                (hasVar ? variableCert : replaceable).Add(b);
            }
            foreach (var b in replaceable)
                writer.ReplaceCertificate(b, certPath, keyPath);
            foreach (var b in variableCert)
                _log.LogWarning(
                    "域名 {Domain} 的 ssl_certificate 使用变量表达式（动态证书），跳过自动替换，请手动处理: {File}:{Line}",
                    domain, b.FilePath, b.StartLine);

            // 边界：listen 带 ssl 但块内无 ssl_certificate（可能由 include 引入）→ 跳过并警告
            foreach (var b in sslListenOnly)
                _log.LogWarning(
                    "域名 {Domain} 存在 SSL 监听但缺少 ssl_certificate（可能由 include 引入），跳过自动修改，请手动配置: {File}:{Line}",
                    domain, b.FilePath, b.StartLine);

            // 场景 3：该域名无任何 SSL 配置 → 基于 80 块创建 443 块
            if (replaceable.Count == 0 && sslListenOnly.Count == 0 && variableCert.Count == 0 && httpBlocks.Count > 0)
            {
                if (!await HasSslModuleAsync(ct))
                {
                    skipped.Add(domain + " -- Nginx 未编译 ssl 模块（nginx -V 缺少 --with-http_ssl_module），无法创建 443 配置");
                    continue;
                }
                // 优先选非重定向且有内容的块做模板；全部纯重定向 → 提示手动配置
                var template = httpBlocks.FirstOrDefault(b => !b.IsRedirectOnly && b.HasContent) ?? httpBlocks[0];
                if (template.IsRedirectOnly)
                {
                    skipped.Add(domain + " -- 80 端口为纯重定向块，无法自动创建 443，请手动配置（" + template.FilePath + ":" + template.StartLine + "）");
                    continue;
                }
                writer.AddHttpsServer(template, certPath, keyPath); // SAN 多域名命中同一块时内部去重
                deployed.AddRange(hitNames);
                _log.LogInformation("域名 {Domain}：已基于 {File}:{Line} 创建 443 配置", domain, template.FilePath, template.StartLine);
            }
            else if (replaceable.Count > 0)
            {
                deployed.AddRange(hitNames);
            }
            else
            {
                var reasons = new List<string>();
                if (variableCert.Count > 0) reasons.Add("ssl_certificate 使用变量表达式（动态证书），跳过自动替换");
                if (sslListenOnly.Count > 0) reasons.Add("存在 SSL 监听但缺少 ssl_certificate，请手动配置");
                skipped.Add(domain + " -- " + string.Join("；", reasons));
            }
        }

        if (deployed.Count == 0)
        {
            LastError = "Nginx 部署失败：未更新任何配置。跳过：" + string.Join("; ", skipped);
            _log.LogError("{Error}", LastError);
            return (false, null);
        }

        // 4. 应用修改 → nginx -t 校验 → 失败自动回滚（回滚后二次校验，确认恢复成功）
        if (writer.HasChanges)
        {
            var (applyOk, applyErr) = await writer.ApplyAllAsync(ct);
            if (!applyOk)
            {
                var rollbackOk = await writer.RollbackAsync();
                LastError = rollbackOk
                    ? "Nginx 配置写入失败，已回滚: " + applyErr
                    : "Nginx 配置写入失败且回滚异常，配置可能已损坏，请立即手动检查 /etc/nginx（备份目录 " + writer.BackupBase + "）: " + applyErr;
                _log.LogError("{Error}", LastError);
                return (false, null);
            }
            var bin = _nginxBin ??= FindNginxBin() ?? "nginx";
            var (tOk, tText) = await Proc.Exec(bin, "-t", ct);
            if (!tOk)
            {
                var rollbackOk = await writer.RollbackAsync();
                if (!rollbackOk)
                {
                    LastError = "nginx -t 校验失败且回滚异常，配置可能已损坏，请立即手动检查 /etc/nginx（备份目录 " + writer.BackupBase + "）:\n" + tText;
                }
                else
                {
                    // 二次校验：确认回滚后的配置可加载，否则说明恢复本身有问题
                    var (rOk, rText) = await Proc.Exec(bin, "-t", ct);
                    LastError = rOk
                        ? "nginx -t 校验失败，已自动回滚配置:\n" + tText
                        : "nginx -t 校验失败，已回滚但回滚后配置仍无法通过校验，请手动检查配置（备份目录 " + writer.BackupBase + "）:\n" + rText;
                }
                _log.LogError("{Error}", LastError);
                return (false, null);
            }
            _log.LogInformation("nginx -t 校验通过");
        }

        foreach (var s in skipped)
            _log.LogWarning("跳过域名: {Reason}", s);

        _log.LogInformation("Nginx 证书部署完成：{Domains}，证书目录={Dir}", string.Join(", ", deployed), dir);
        return (true, deployed.ToArray());
    }

    public async Task<bool> ReloadAsync(CancellationToken ct)
    {
        var bin = _nginxBin ??= FindNginxBin() ?? "nginx";
        var (ok, text) = await Proc.Exec(bin, "-t", ct);
        if (!ok) { _log.LogError("nginx -t 配置检查失败:\n{Text}", text); return false; }
        (ok, text) = await Proc.Exec(bin, "-s reload", ct);
        if (ok) _log.LogInformation("Nginx 重载完成");
        else _log.LogError("Nginx 重载失败:\n{Text}", text);
        return ok;
    }

    /// <summary>检测 Nginx 是否编译了 ssl 模块（创建 443 配置的前置条件；结果缓存，避免每个域名重复执行 nginx -V）</summary>
    private async Task<bool> HasSslModuleAsync(CancellationToken ct)
    {
        if (_sslModuleOk.HasValue) return _sslModuleOk.Value;
        var bin = _nginxBin ??= FindNginxBin();
        if (bin is null) return false;
        var (ok, output) = await Proc.Exec(bin, "-V", ct);
        _sslModuleOk = ok && output.Contains("--with-http_ssl_module", StringComparison.OrdinalIgnoreCase);
        return _sslModuleOk.Value;
    }

    private static string? FindNginxBin()
    {
        string[] paths = { "/usr/sbin/nginx", "/usr/bin/nginx", "/usr/local/nginx/sbin/nginx" };
        foreach (var p in paths)
            if (File.Exists(p)) return p;
        return null;
    }
}

public class ApacheProvider : IDeployProvider
{
    public string Name => "apache";
    public string? LastError { get; private set; }
    public bool IsAvailable => File.Exists("/usr/sbin/apache2") || File.Exists("/usr/sbin/httpd");

    private readonly ILogger<ApacheProvider> _log;
    private readonly string _base;
    private bool? _sslModuleOk;

    public ApacheProvider(ILogger<ApacheProvider> log, string? basePath = null)
    {
        _log = log;
        // 按发行版探测默认证书目录：RedHat 系 /etc/httpd/ssl，Debian 系 /etc/apache2/ssl
        _base = basePath ?? (Directory.Exists("/etc/httpd") ? "/etc/httpd/ssl" : "/etc/apache2/ssl");
    }

    public async Task<(bool ok, string[]? deployedDomains)> DeployAsync(string certPem, string keyPem, string[] domains, CancellationToken ct)
    {
        LastError = null;
        // 主域名目录：优先取第一个非通配符条目（与 Nginx 对齐）
        var primaryDomain = CertFile.PrimaryDomain(domains);

        // 1. 写入证书（原子写 + 私钥 600）
        var dir = Path.Combine(_base, primaryDomain);
        Directory.CreateDirectory(dir);
        var certPath = Path.Combine(dir, "certificate.crt");
        var keyPath = Path.Combine(dir, "private.key");
        await CertFile.WriteAsync(certPath, certPem, ct);
        await CertFile.WriteAsync(keyPath, keyPem, ct);
        _log.LogInformation("Apache 证书已写入: {Domain} -> {Dir}", primaryDomain, dir);

        // 2. 解析 VirtualHost + 全局 Listen 端口
        var parser = new ApacheConfigParser(_log);
        var vhosts = await parser.LoadVirtualHostsAsync(ct);
        if (vhosts.Count == 0)
        {
            LastError = "Apache 部署失败：未发现任何 VirtualHost 配置（请确认 apache2.conf 及 Include 路径可读）";
            _log.LogError("{Error}", LastError);
            return (false, null);
        }
        var listenPorts = parser.FindListenPorts();
        _log.LogInformation("Apache 解析到 {Count} 个 VirtualHost，Listen 端口: {Ports}", vhosts.Count, string.Join(", ", listenPorts));

        // 3. 逐域名处理（四场景，与 Nginx 对齐）
        var writer = new ApacheConfigWriter(_log)
        {
            BackupBase = Path.Combine(_base, ".backup")
        };
        var deployed = new List<string>();
        var skipped = new List<string>();

        foreach (var domain in domains)
        {
            var matched = vhosts.Where(v => ApacheConfigParser.MatchesDomain(v, domain)).ToList();
            if (matched.Count == 0)
            {
                skipped.Add(domain + " -- Apache 上无站点服务此域名");
                continue;
            }

            // 实际命中的主机名（通配符条目展开为具体站点名，用于平台上报，与 Nginx 对齐）
            var hitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in matched)
                foreach (var n in v.ServerNames)
                    if (NginxConfigParser.DomainMatch(n, domain))
                        hitNames.Add(n);
            if (hitNames.Count == 0) hitNames.Add(domain);

            var sslBlocks = matched.Where(v => v.HasSslCertificate).ToList();
            var sslListenOnly = matched.Where(v => !v.HasSslCertificate && (v.HasSslListen || v.HasSslEngine)).ToList();
            var httpBlocks = matched.Where(v => !v.HasSslCertificate && !v.HasSslListen && !v.HasSslEngine).ToList();

            // 场景 2/4：匹配块内已配置证书（任何端口）→ 全部替换为当前安装证书；
            // 含变量表达式（动态证书）的块跳过替换并警告
            var replaceable = new List<ApacheVirtualHost>();
            var variableCert = new List<ApacheVirtualHost>();
            foreach (var v in sslBlocks)
            {
                var hasVar = v.SslCertificateFiles.Any(f => f.Contains('$'))
                             || v.SslCertificateKeyFiles.Any(f => f.Contains('$'));
                (hasVar ? variableCert : replaceable).Add(v);
            }
            foreach (var v in replaceable) writer.ReplaceCertificate(v, certPath, keyPath);
            foreach (var v in variableCert)
                _log.LogWarning(
                    "域名 {Domain} 的 SSLCertificateFile 使用变量表达式（动态证书），跳过自动替换，请手动处理: {File}:{Line}",
                    domain, v.FilePath, v.StartLine);

            // 边界：443 监听但缺证书（可能由 Include 引入）→ 跳过并警告
            foreach (var v in sslListenOnly)
                _log.LogWarning(
                    "域名 {Domain} 存在 443 监听但缺少 SSLCertificateFile（可能由 Include 引入），跳过自动修改，请手动配置: {File}:{Line}",
                    domain, v.FilePath, v.StartLine);

            // 场景 3：该域名无任何 SSL 配置 → 基于 80 块创建 443 VirtualHost
            if (replaceable.Count == 0 && sslListenOnly.Count == 0 && variableCert.Count == 0 && httpBlocks.Count > 0)
            {
                if (!await HasSslModuleAsync(ct))
                {
                    skipped.Add(domain + " -- Apache 未启用 mod_ssl（无法使用 SSLEngine），无法创建 443 VirtualHost");
                    continue;
                }
                // 优先选非重定向且有内容的块做模板；全部纯重定向 → 提示手动配置
                var template = httpBlocks.FirstOrDefault(v => !v.IsRedirectOnly && v.HasContent) ?? httpBlocks[0];
                if (template.IsRedirectOnly)
                {
                    skipped.Add(domain + " -- 80 端口为纯重定向块，无法自动创建 443，请手动配置（" + template.FilePath + ":" + template.StartLine + "）");
                    continue;
                }
                // Apache 的 VirtualHost 端口需要全局 Listen 支持，缺少 443 时自动补充
                var ensureListen443 = !listenPorts.Contains(443);
                writer.AddHttpsVirtualHost(template, certPath, keyPath, ensureListen443);
                deployed.AddRange(hitNames);
                _log.LogInformation("域名 {Domain}：已基于 {File}:{Line} 创建 443 VirtualHost（补 Listen 443={Ensure}）",
                    domain, template.FilePath, template.StartLine, ensureListen443);
            }
            else if (replaceable.Count > 0)
            {
                deployed.AddRange(hitNames);
            }
            else
            {
                var reasons = new List<string>();
                if (variableCert.Count > 0) reasons.Add("SSLCertificateFile 使用变量表达式（动态证书），跳过自动替换");
                if (sslListenOnly.Count > 0) reasons.Add("存在 443 监听但缺少 SSLCertificateFile，请手动配置");
                skipped.Add(domain + " -- " + string.Join("；", reasons));
            }
        }

        if (deployed.Count == 0)
        {
            LastError = "Apache 部署失败：未更新任何配置。跳过：" + string.Join("; ", skipped);
            _log.LogError("{Error}", LastError);
            return (false, null);
        }

        // 4. 应用修改 → configtest 校验 → 失败自动回滚（回滚后二次校验）
        if (writer.HasChanges)
        {
            var (applyOk, applyErr) = await writer.ApplyAllAsync(ct);
            if (!applyOk)
            {
                var rollbackOk = await writer.RollbackAsync();
                LastError = rollbackOk
                    ? "Apache 配置写入失败，已回滚: " + applyErr
                    : "Apache 配置写入失败且回滚异常，配置可能已损坏，请立即手动检查（备份目录 " + writer.BackupBase + "）: " + applyErr;
                _log.LogError("{Error}", LastError);
                return (false, null);
            }
            var (tOk, tText) = await ConfigTestAsync(ct);
            if (!tOk)
            {
                var rollbackOk = await writer.RollbackAsync();
                if (!rollbackOk)
                {
                    LastError = "Apache 配置校验失败且回滚异常，配置可能已损坏，请立即手动检查（备份目录 " + writer.BackupBase + "）:\n修改后校验:\n" + tText;
                }
                else
                {
                    var (rOk, rText) = await ConfigTestAsync(ct);
                    LastError = rOk
                        ? "Apache 配置校验失败，已自动回滚配置:\n" + tText
                        : "Apache 配置校验失败，已回滚但回滚后配置仍无法通过校验，请手动检查配置与证书文件（备份目录 " + writer.BackupBase + "）:\n\n[修改后校验输出]\n" + tText + "\n\n[回滚后校验输出]\n" + rText;
                }
                _log.LogError("{Error}", LastError);
                return (false, null);
            }
            _log.LogInformation("Apache 配置校验通过（Syntax OK）");
        }

        foreach (var s in skipped)
            _log.LogWarning("跳过域名: {Reason}", s);

        _log.LogInformation("Apache 证书部署完成：{Domains}，证书目录={Dir}", string.Join(", ", deployed), dir);
        return (true, deployed.ToArray());
    }

    public async Task<bool> ReloadAsync(CancellationToken ct)
    {
        var bin = FindApacheBin();
        var (ok, text) = await Proc.Exec(bin, "graceful", ct);
        if (!ok) (ok, text) = await Proc.Exec(bin, "-k graceful", ct);
        if (ok) _log.LogInformation("Apache 重载完成");
        else _log.LogError("Apache 重载失败:\n{Text}", text);
        return ok;
    }

    /// <summary>配置校验：apache2ctl configtest / httpd -t（对齐 nginx -t）</summary>
    private async Task<(bool ok, string output)> ConfigTestAsync(CancellationToken ct)
    {
        var bin = FindApacheBin();
        var (ok, text) = await Proc.Exec(bin, "configtest", ct);
        if (!ok) (ok, text) = await Proc.Exec(bin, "-t", ct);
        return (ok, text);
    }

    /// <summary>检测 mod_ssl 是否加载（创建 443 VirtualHost 前置条件；结果缓存）</summary>
    private async Task<bool> HasSslModuleAsync(CancellationToken ct)
    {
        if (_sslModuleOk.HasValue) return _sslModuleOk.Value;
        var bin = FindApacheBin();
        var (ok, output) = await Proc.Exec(bin, "-M", ct);
        _sslModuleOk = ok && output.Contains("ssl_module", StringComparison.OrdinalIgnoreCase);
        return _sslModuleOk.Value;
    }

    private static string FindApacheBin()
    {
        string[] paths = { "/usr/sbin/apache2ctl", "/usr/sbin/apache2", "/usr/sbin/httpd" };
        foreach (var p in paths)
            if (File.Exists(p)) return p;
        return "apache2ctl";
    }
}

public class IisProvider : IDeployProvider
{
    public string Name => "iis";
    public string? LastError { get; private set; }
    public bool IsAvailable => OperatingSystem.IsWindows() && File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "appcmd.exe"));

    private readonly ILogger<IisProvider> _log;
    private readonly string _appCmd;

    public IisProvider(ILogger<IisProvider> log)
    {
        _log = log;
        _appCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "appcmd.exe");
    }

    public async Task<bool> ReloadAsync(CancellationToken ct)
    {
        // IIS 的 SSL 终止在内核 HTTP.sys。DeployAsync 中已通过 appcmd / PowerShell
        // 把新证书指纹提交到 HTTPS 绑定，HTTP.sys 立即按新指纹处理新的 TLS 连接，
        // 无需重启站点、无需回收应用池、无需 iisreset。
        // 此处对齐微软 IIS Administration API 的实现约定：仅 CommitChanges，不 iisreset、不 recycle。
        //
        // 边界取舍：若应用在进程内自行缓存证书（如 Kestrel/HttpListener 自托管、
        // ASP.NET 对外 mTLS 使用 X509Certificate2），这类证书不会随绑定更新而刷新，
        // 需各自重载；本 Agent 不自动处理。
        _log.LogInformation("IIS 无需重载：HTTPS 绑定已即时生效（对齐微软 IIS Administration API，未执行 iisreset/回收）");
        await Task.CompletedTask;
        return true;
    }

    public async Task<(bool ok, string[]? deployedDomains)> DeployAsync(string certPem, string keyPem, string[] domains, CancellationToken ct)
    {
        LastError = null;
        var primaryDomain = domains.Length > 0 ? domains[0] : "unknown";

        var pfxPath = await CreatePfxAsync(certPem, keyPem, primaryDomain, ct);
        if (pfxPath is null)
        {
            LastError = "创建 PFX 失败: " + primaryDomain;
            return (false, null);
        }

        try
        {
            _log.LogInformation("正在导入 IIS 证书...");
            var (ok, text) = await Proc.Exec("certutil", "-f -p \"\" -importpfx My \"" + pfxPath + "\"", ct);
            if (!ok)
            {
                LastError = "证书导入失败: " + text;
                _log.LogError("certutil 导入失败:\n{Text}", text);
                return (false, null);
            }

            var hash = GetCertThumbprint(pfxPath);
            if (string.IsNullOrEmpty(hash))
            {
                LastError = "无法获取导入证书的指纹";
                _log.LogError("无法从导入的 PFX 获取证书指纹");
                return (false, null);
            }

            (ok, text) = await Proc.Exec(_appCmd, "list site", ct);
            if (!ok)
            {
                LastError = "获取 IIS 站点列表失败: " + text;
                _log.LogError("appcmd list site 失败:\n{Text}", text);
                return (false, null);
            }
            var sites = ParseSites(text);
            _log.LogInformation("解析到 {Count} 个 IIS 站点", sites.Count);
            foreach (var s in sites)
                _log.LogInformation("  站点: [{Id}] {Name}", s.id, s.name);

            var allSiteBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var listSiteLines = text.Split('\n');
            foreach (var (siteName, _) in sites)
            {
                var siteLine = listSiteLines.FirstOrDefault(l => l.Contains("\"" + siteName + "\""));
                if (siteLine != null)
                {
                    var bindings = ExtractBindingsFromSiteLine(siteLine);
                    allSiteBindings[siteName] = bindings;
                    _log.LogInformation("站点 '{Name}' 绑定数据:\n{Data}", siteName, bindings);
                }
                else
                {
                    _log.LogWarning("无法找到站点 '{Name}' 的原始输出行", siteName);
                }
            }

            var updated = 0;
            var skipped = new List<string>();
            var deployed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var domain in domains)
            {
                _log.LogInformation("域名 '{Domain}': 检查 {Count} 个站点的绑定", domain, allSiteBindings.Count);
                var matchedSites = new List<string>();

                foreach (var (siteName, bindings) in allSiteBindings)
                {
                    _log.LogInformation("  检查站点 '{Site}' 绑定是否包含域名 '{Domain}'", siteName, domain);
                    if (SiteHasBindingForDomain(bindings, domain))
                    {
                        _log.LogInformation("  [√] 匹配: {Site}", siteName);
                        matchedSites.Add(siteName);
                    }
                }

                if (matchedSites.Count == 0)
                {
                    skipped.Add(domain + " -- IIS 上无站点服务此域名");
                    continue;
                }

                foreach (var siteName in matchedSites)
                {
                    var bindings = allSiteBindings[siteName];

                    // 如果已有 HTTPS 绑定则删除（避免端口/域名冲突），否则跳过删除
                    var hasHttpsBinding = SiteHasHttpsBindingForDomain(bindings, domain);
                    if (hasHttpsBinding)
                    {
                        await Proc.Exec(_appCmd, "set site /site.name:\"" + siteName + "\" /-bindings.[protocol='https',bindingInformation='*:443:" + domain + "']", ct);
                    }

                    // 新建 HTTPS 绑定（不含 sslFlags，避免 appcmd 解析非键属性失败）
                    var (addOk, addText) = await Proc.Exec(_appCmd, "set site /site.name:\"" + siteName + "\" /+bindings.[protocol='https',bindingInformation='*:443:" + domain + "']", ct);
                    if (!addOk) { skipped.Add(domain + "@" + siteName + " -- 新建 HTTPS 绑定失败"); continue; }
                    var psScript = "Import-Module WebAdministration; " +
                        "$b = Get-WebBinding -Name '" + siteName + "' -Protocol 'https' -HostHeader '" + domain + "'; " +
                        "if ($b) { " +
                        "   $b.AddSslCertificate('" + hash + "', 'MY'); " +
                        "} else { throw 'HTTPS binding not found' }";
                    // appcmd 设置 SNI 标志（set 语法直接设置集合元素属性，不同于 /+ 添加语法）
                    var (sniOk, sniText) = await Proc.Exec(_appCmd, "set site /site.name:\"" + siteName + "\" /bindings.[protocol='https',bindingInformation='*:443:" + domain + "'].sslFlags:1", ct);
                    if (!sniOk)
                    {
                        _log.LogWarning("appcmd 启用 SNI 失败，跳过 SNI 设置: {Text}", sniText);
                    }
                    var (psOk, psText) = await Proc.Exec("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + psScript + "\"", ct);
                    if (psOk) { updated++; deployed.Add(domain); }
                    else { _log.LogError("PowerShell 分配证书失败: {Text}", psText); skipped.Add(domain + "@" + siteName + " -- 分配证书失败"); }
                }
            }

            if (deployed.Count == 0)
            {
                var skipDetail = string.Join("; ", skipped);
                LastError = "IIS 部署失败：未更新任何绑定。跳过：" + skipDetail;
                _log.LogError("IIS 部署失败：未更新任何绑定。跳过：{Skipped}", skipDetail);
                return (false, null);
            }

            _log.LogInformation(
                "IIS 证书部署完成：{Domains}，哈希={Hash}。更新 {Updated} 个绑定，跳过 {SkippedCount} 个。",
                string.Join(", ", deployed), hash.Substring(0, Math.Min(8, hash.Length)), updated, skipped.Count);

            return (true, deployed.ToArray());
        }
        finally
        {
            try { File.Delete(pfxPath); } catch { }
        }
    }

    private async Task<string?> CreatePfxAsync(string certPem, string keyPem, string domain, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "CertGuard", domain);
        Directory.CreateDirectory(tempDir);
        var certFile = Path.Combine(tempDir, "cert.pem");
        var keyFile = Path.Combine(tempDir, "key.pem");
        var pfxFile = Path.Combine(tempDir, "cert.pfx");

        await File.WriteAllTextAsync(certFile, certPem, ct);
        await File.WriteAllTextAsync(keyFile, keyPem, ct);

        try
        {
            // 从 PEM 文件创建 X509Certificate2 并导出为 PFX
            using var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certFile, keyFile);
            var data = cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12);
            await File.WriteAllBytesAsync(pfxFile, data, ct);
        }
        catch (Exception ex)
        {
            _log.LogError("创建 PFX 失败: {Message}", ex.Message);
            try { File.Delete(certFile); File.Delete(keyFile); } catch { }
            return null;
        }

        try { File.Delete(certFile); File.Delete(keyFile); } catch { }
        return pfxFile;
    }

    private static string? GetCertThumbprint(string pfxPath)
    {
        try
        {
            using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(pfxPath);
            return cert.Thumbprint;
        }
        catch { return null; }
    }

    private static string ExtractBindingsFromSiteLine(string siteLine)
    {
        var m = Regex.Match(siteLine, @"bindings:(.+?)(?:\)|$)");
        if (!m.Success) return "";
        var bindingsPart = m.Groups[1].Value.TrimEnd(',');
        if (string.IsNullOrEmpty(bindingsPart)) return "";
        var entries = bindingsPart.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        foreach (var entry in entries)
        {
            var trimmed = entry.Trim();
            // 只保留 protocol/bindingInfo 格式的绑定行，过滤掉 state:Started 等非绑定属性
            if (trimmed.Contains('/') && trimmed.Contains(':'))
            {
                result.Add(trimmed);
            }
        }
        return string.Join("\n", result);
    }

    private static List<(string name, string id)> ParseSites(string appcmdOutput)
    {
        var result = new List<(string, string)>();
        foreach (var line in appcmdOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            var m = Regex.Match(trimmed, @"^SITE\s+""([^""]+)""\s+\(id:(\d+)");
            if (m.Success)
            {
                result.Add((m.Groups[1].Value, m.Groups[2].Value));
            }
        }
        return result;
    }

    private static string ExtractHostFromBindingLine(string line)
    {
        // 格式: protocol/bindingInfo, 例如 http/*:80:xxx.cn
        var slashIdx = line.IndexOf('/');
        if (slashIdx < 0) return "";
        var info = line.Substring(slashIdx + 1);
        var parts = info.Split(':');
        return parts.Length >= 3 ? parts[2] : "";
    }

    private static string ExtractProtocolFromBindingLine(string line)
    {
        // 格式: protocol/bindingInfo, 例如 http/*:80:xxx.cn
        var slashIdx = line.IndexOf('/');
        if (slashIdx < 0) return "";
        return line.Substring(0, slashIdx);
    }

    private static bool DomainMatches(string domain, string host)
    {
        return string.Equals(domain, host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SiteHasBindingForDomain(string bindingsOutput, string domain)
    {
        foreach (var line in bindingsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var host = ExtractHostFromBindingLine(line);
            if (string.IsNullOrEmpty(host)) continue;
            if (DomainMatches(domain, host)) return true;
        }
        return false;
    }

    private static bool SiteHasHttpsBindingForDomain(string bindingsOutput, string domain)
    {
        foreach (var line in bindingsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var protocol = ExtractProtocolFromBindingLine(line);
            if (protocol != "https") continue;
            var host = ExtractHostFromBindingLine(line);
            if (string.IsNullOrEmpty(host)) continue;
            if (DomainMatches(domain, host)) return true;
        }
        return false;
    }
}

public class NoopProvider : IDeployProvider
{
    public string Name => "none";
    public string? LastError => null;
    public bool IsAvailable => false;

    public Task<(bool ok, string[]? deployedDomains)> DeployAsync(string certPem, string keyPem, string[] domains, CancellationToken ct)
        => Task.FromResult((false, (string[]?)null));

    public Task<bool> ReloadAsync(CancellationToken ct)
        => Task.FromResult(false);
}

public class ProviderFactory
{
    private readonly ILoggerFactory _logFactory;

    public ProviderFactory(ILoggerFactory logFactory)
    {
        _logFactory = logFactory;
    }

    public (IDeployProvider provider, string osType, string osVer) Create()
    {
        var osType = GetOsType();
        var osVer = GetOsVersion();

        if (OperatingSystem.IsWindows())
        {
            var iis = new IisProvider(_logFactory.CreateLogger<IisProvider>());
            if (iis.IsAvailable) return (iis, osType, osVer);
        }
        else
        {
            var nginx = new NginxProvider(_logFactory.CreateLogger<NginxProvider>());
            if (nginx.IsAvailable) return (nginx, osType, osVer);

            var apache = new ApacheProvider(_logFactory.CreateLogger<ApacheProvider>());
            if (apache.IsAvailable) return (apache, osType, osVer);
        }

        return (new NoopProvider(), osType, osVer);
    }

    private static string GetOsType()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsLinux()) return "Linux";
        if (OperatingSystem.IsMacOS()) return "macOS";
        return "Unknown";
    }

    private static string GetOsVersion()
    {
        return Environment.OSVersion.VersionString;
    }
}
