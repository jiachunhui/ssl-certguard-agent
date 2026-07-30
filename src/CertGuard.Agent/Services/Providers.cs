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

public class NginxProvider : IDeployProvider
{
    public string Name => "nginx";
    public string? LastError { get; private set; }
    public bool IsAvailable =>
        File.Exists("/usr/sbin/nginx") || File.Exists("/usr/bin/nginx") || File.Exists("/usr/local/nginx/sbin/nginx");

    private readonly ILogger<NginxProvider> _log;
    private readonly string _base;
    private string? _nginxBin;

    public NginxProvider(ILogger<NginxProvider> log, string? basePath = null)
    { _log = log; _base = basePath ?? "/etc/nginx/ssl"; }

    public async Task<(bool ok, string[]? deployedDomains)> DeployAsync(string certPem, string keyPem, string[] domains, CancellationToken ct)
    {
        LastError = null;
        var primaryDomain = domains.Length > 0 ? domains[0] : "unknown";

        // 扫描 Nginx 配置中的 server_name
        var serverNames = await GetServerNamesAsync(ct);
        _log.LogInformation("Nginx 中发现 {Count} 个 server_name: {Names}", serverNames.Count, string.Join(", ", serverNames));

        var matched = new List<string>();
        var skipped = new List<string>();

        foreach (var domain in domains)
        {
            if (serverNames.Contains(domain))
                matched.Add(domain);
            else
                skipped.Add(domain + " -- Nginx 上无站点服务此域名");
        }

        if (matched.Count == 0)
        {
            LastError = "Nginx 部署失败：未找到匹配的站点。跳过：" + string.Join("; ", skipped);
            _log.LogError("{Error}", LastError);
            return (false, null);
        }

        foreach (var s in skipped)
            _log.LogWarning("跳过域名: {Reason}", s);

        // 写入证书
        var dir = Path.Combine(_base, primaryDomain);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "fullchain.pem"), certPem, ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "privkey.pem"), keyPem, ct);
        var keyPath = Path.Combine(dir, "privkey.pem");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _log.LogInformation("Nginx 证书已写入: {Domain} -> {Dir}", primaryDomain, dir);
        return (true, matched.ToArray());
    }

    public async Task<bool> ReloadAsync(CancellationToken ct)
    {
        var (ok, text) = await Proc.Exec("nginx", "-t", ct);
        if (!ok) { _log.LogError("nginx -t 配置检查失败:\n{Text}", text); return false; }
        (ok, text) = await Proc.Exec("nginx", "-s reload", ct);
        if (ok) _log.LogInformation("Nginx 重载完成");
        else _log.LogError("Nginx 重载失败:\n{Text}", text);
        return ok;
    }

    // ---- 以下为 Nginx 站点扫描辅助方法 ----

    private static List<string> GetNginxConfigFiles()
    {
        var files = new List<string>();
        string[] searchDirs = { "/etc/nginx/sites-enabled", "/etc/nginx/conf.d" };
        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir))
            {
                var ext = Path.GetExtension(f);
                if (string.IsNullOrEmpty(ext) || ext == ".conf")
                    files.Add(f);
            }
        }
        if (File.Exists("/etc/nginx/nginx.conf"))
            files.Add("/etc/nginx/nginx.conf");
        return files;
    }

    private static HashSet<string> ParseServerNames(string configText)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var regex = new Regex(@"server_name\s+([^;]+);", RegexOptions.IgnoreCase);
        foreach (Match m in regex.Matches(configText))
        {
            foreach (var name in m.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = name.Trim();
                if (!string.IsNullOrEmpty(trimmed) && trimmed != "_")
                    names.Add(trimmed);
            }
        }
        return names;
    }

    private async Task<string?> ReadNginxFullConfigAsync(CancellationToken ct)
    {
        var bin = _nginxBin ??= FindNginxBin();
        if (bin is null) return null;
        var (ok, output) = await Proc.Exec(bin, "-T", ct);
        return ok ? output : null;
    }

    private static string? FindNginxBin()
    {
        string[] paths = { "/usr/sbin/nginx", "/usr/bin/nginx", "/usr/local/nginx/sbin/nginx" };
        foreach (var p in paths)
            if (File.Exists(p)) return p;
        return null;
    }

    private async Task<HashSet<string>> GetServerNamesAsync(CancellationToken ct)
    {
        // 先尝试扫描配置文件
        var files = GetNginxConfigFiles();
        if (files.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                try { sb.AppendLine(await File.ReadAllTextAsync(f, ct)); }
                catch (Exception ex) { _log.LogDebug("无法读取 {File}: {Error}", f, ex.Message); }
            }
            var names = ParseServerNames(sb.ToString());
            if (names.Count > 0) return names;
        }

        // 回退：使用 nginx -T 导出完整配置
        var fullConfig = await ReadNginxFullConfigAsync(ct);
        if (!string.IsNullOrEmpty(fullConfig))
            return ParseServerNames(fullConfig);

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}

public class ApacheProvider : IDeployProvider
{
    public string Name => "apache";
    public string? LastError { get; private set; }
    public bool IsAvailable => File.Exists("/usr/sbin/apache2") || File.Exists("/usr/sbin/httpd");

    private readonly ILogger<ApacheProvider> _log;
    private readonly string _base;

    public ApacheProvider(ILogger<ApacheProvider> log, string? basePath = null)
    { _log = log; _base = basePath ?? "/etc/apache2/ssl"; }

    public async Task<(bool ok, string[]? deployedDomains)> DeployAsync(string certPem, string keyPem, string[] domains, CancellationToken ct)
    {
        LastError = null;
        var primaryDomain = domains.Length > 0 ? domains[0] : "unknown";

        // 扫描 Apache 配置中的 ServerName / ServerAlias
        var serverNames = await GetApacheServerNamesAsync(ct);
        _log.LogInformation("Apache 中发现 {Count} 个主机名: {Names}", serverNames.Count, string.Join(", ", serverNames));

        var matched = new List<string>();
        var skipped = new List<string>();

        foreach (var domain in domains)
        {
            if (serverNames.Contains(domain))
                matched.Add(domain);
            else
                skipped.Add(domain + " -- Apache 上无站点服务此域名");
        }

        if (matched.Count == 0)
        {
            LastError = "Apache 部署失败：未找到匹配的站点。跳过：" + string.Join("; ", skipped);
            _log.LogError("{Error}", LastError);
            return (false, null);
        }

        foreach (var s in skipped)
            _log.LogWarning("跳过域名: {Reason}", s);

        // 写入证书
        var dir = Path.Combine(_base, primaryDomain);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "certificate.crt"), certPem, ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "private.key"), keyPem, ct);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(Path.Combine(dir, "private.key"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _log.LogInformation("Apache 证书已写入: {Domain} -> {Dir}", primaryDomain, dir);
        return (true, matched.ToArray());
    }

    public async Task<bool> ReloadAsync(CancellationToken ct)
    {
        var (ok, text) = await Proc.Exec("apache2ctl", "graceful", ct);
        if (!ok) (ok, text) = await Proc.Exec("httpd", "-k graceful", ct);
        if (ok) _log.LogInformation("Apache 重载完成");
        else _log.LogError("Apache 重载失败:\n{Text}", text);
        return ok;
    }

    // ---- Apache 站点扫描辅助方法 ----

    private static List<string> GetApacheConfigFiles()
    {
        var files = new List<string>();
        string[] searchDirs = { "/etc/apache2/sites-enabled", "/etc/httpd/conf.d" };
        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir))
            {
                var ext = Path.GetExtension(f);
                if (string.IsNullOrEmpty(ext) || ext == ".conf")
                    files.Add(f);
            }
        }
        if (File.Exists("/etc/apache2/apache2.conf"))
            files.Add("/etc/apache2/apache2.conf");
        else if (File.Exists("/etc/httpd/conf/httpd.conf"))
            files.Add("/etc/httpd/conf/httpd.conf");
        return files;
    }

    private static HashSet<string> ParseApacheServerNames(string configText)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // ServerName directive
        foreach (Match m in Regex.Matches(configText, @"ServerName\s+(\S+)", RegexOptions.IgnoreCase))
        {
            var name = m.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(name) && name != "localhost")
                names.Add(name);
        }
        // ServerAlias directive (space-separated)
        foreach (Match m in Regex.Matches(configText, @"ServerAlias\s+([^\n]+)", RegexOptions.IgnoreCase))
        {
            foreach (var alias in m.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = alias.Trim();
                if (!string.IsNullOrEmpty(trimmed) && trimmed != "localhost")
                    names.Add(trimmed);
            }
        }
        return names;
    }

    private async Task<string?> ReadApacheFullConfigAsync(CancellationToken ct)
    {
        string[] bins = { "/usr/sbin/apache2ctl", "/usr/sbin/apache2", "/usr/sbin/httpd" };
        foreach (var bin in bins)
        {
            if (!File.Exists(bin)) continue;
            var (ok, output) = await Proc.Exec(bin, "-S", ct);
            if (ok && !string.IsNullOrEmpty(output)) return output;
        }
        return null;
    }

    private static HashSet<string> ParseApacheCtlOutput(string output)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            // 去除配置文件路径（括号部分），避免误匹配路径中的域名
            var clean = Regex.Replace(line, @"\([^)]+\)", "");
            foreach (Match m in Regex.Matches(clean, @"\b([a-zA-Z0-9]([a-zA-Z0-9\-]*[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}\b"))
            {
                names.Add(m.Value);
            }
        }
        return names;
    }

    private async Task<HashSet<string>> GetApacheServerNamesAsync(CancellationToken ct)
    {
        // 先尝试扫描配置文件
        var files = GetApacheConfigFiles();
        if (files.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                try { sb.AppendLine(await File.ReadAllTextAsync(f, ct)); }
                catch (Exception ex) { _log.LogDebug("无法读取 {File}: {Error}", f, ex.Message); }
            }
            var names = ParseApacheServerNames(sb.ToString());
            if (names.Count > 0) return names;
        }

        // 回退：使用 apache2ctl -S / httpd -S 导出虚拟主机配置
        var fullConfig = await ReadApacheFullConfigAsync(ct);
        if (!string.IsNullOrEmpty(fullConfig))
            return ParseApacheCtlOutput(fullConfig);

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        var (ok, text) = await Proc.Exec("iisreset", "", ct);
        if (ok) _log.LogInformation("IIS 重载完成");
        else _log.LogError("IIS 重载失败:\n{Text}", text);
        return ok;
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

        throw new InvalidOperationException("未找到可用的部署提供程序");
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
