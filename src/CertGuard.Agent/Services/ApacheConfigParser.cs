// ============================================================
// Services/ApacheConfigParser.cs — Apache 配置解析层
// 职责：配置文件发现（Include 递归展开 + glob + symlink 解析）、
//       VirtualHost 块级解析、ServerName/ServerAlias/SSL 指令提取、
//       Listen 端口收集（创建 443 前置检查）
// ============================================================

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CertGuard.Agent.Services;

/// <summary>VirtualHost 的监听地址（从 &lt;VirtualHost 地址&gt; 解析）</summary>
public sealed class ApacheListenInfo
{
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string Raw { get; set; } = "";
}

/// <summary>解析后的 VirtualHost 块（含行号定位，用于精确改写）</summary>
public sealed class ApacheVirtualHost
{
    /// <summary>真实文件路径（sites-enabled 符号链接已解析）</summary>
    public string FilePath { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Text { get; set; } = "";
    /// <summary>块首行的行首缩进</summary>
    public string Indent { get; set; } = "";
    /// <summary>ServerName + ServerAlias 合并</summary>
    public List<string> ServerNames { get; set; } = new();
    /// <summary>监听地址列表</summary>
    public List<ApacheListenInfo> Addresses { get; set; } = new();
    public List<string> SslCertificateFiles { get; set; } = new();
    public List<string> SslCertificateKeyFiles { get; set; } = new();
    public bool HasSslEngine { get; set; }
    public bool HasRedirect { get; set; }

    /// <summary>块内是否已配置证书（SSLCertificateFile 指令）</summary>
    public bool HasSslCertificate => SslCertificateFiles.Count > 0;
    /// <summary>是否监听 443</summary>
    public bool HasSslListen => Addresses.Any(a => a.Port == 443);
    /// <summary>纯重定向块：有跳转指令但无可服务内容</summary>
    public bool IsRedirectOnly => HasRedirect && !HasContent;
    public bool HasContent { get; set; }
}

/// <summary>Apache 配置解析器</summary>
public sealed class ApacheConfigParser
{
    private readonly ILogger _log;
    private static readonly Regex VhStartRe = new(@"^\s*<VirtualHost\s+([^>]+)>", RegexOptions.IgnoreCase);
    private static readonly Regex VhEndRe = new(@"^\s*</VirtualHost\s*>", RegexOptions.IgnoreCase);
    private static readonly Regex ServerNameRe = new(@"ServerName\s+(\S+)", RegexOptions.IgnoreCase);
    private static readonly Regex ServerAliasRe = new(@"ServerAlias\s+([^\n]+)", RegexOptions.IgnoreCase);
    private static readonly Regex SslCertFileRe = new(@"SSLCertificateFile\s+(\S+)", RegexOptions.IgnoreCase);
    private static readonly Regex SslCertKeyRe = new(@"SSLCertificateKeyFile\s+(\S+)", RegexOptions.IgnoreCase);
    private static readonly Regex SslEngineRe = new(@"SSLEngine\s+(\S+)", RegexOptions.IgnoreCase);
    private static readonly Regex IncludeRe = new(@"^\s*Include(?:Optional)?\s+([^\n]+)", RegexOptions.IgnoreCase);
    private static readonly Regex ListenRe = new(@"^\s*Listen\s+(\S+)", RegexOptions.IgnoreCase);

    private static readonly string[] ContentDirectives =
    {
        "DocumentRoot", "ProxyPass", "ProxyPassReverse", "Alias", "AliasMatch",
        "FastCGI", "SetHandler", "WSGI", "php-fpm", "UserDir"
    };
    private static readonly string[] RedirectDirectives = { "Redirect", "RedirectMatch", "RewriteRule" };

    public ApacheConfigParser(ILogger log) => _log = log;

    /// <summary>主配置路径（Debian / RedHat 自动探测）</summary>
    public static string MainConfigPath { get; set; } =
        File.Exists("/etc/apache2/apache2.conf") ? "/etc/apache2/apache2.conf" : "/etc/httpd/conf/httpd.conf";

    /// <summary>兜底扫描目录</summary>
    public static string[] FallbackDirs { get; set; } =
        { "/etc/apache2/sites-enabled", "/etc/httpd/conf.d" };

    // ── 配置发现（Include 递归展开） ─────────────────────────

    public List<string> DiscoverConfigFiles()
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        if (File.Exists(MainConfigPath)) queue.Enqueue(MainConfigPath);
        foreach (var d in FallbackDirs)
        {
            if (!Directory.Exists(d)) continue;
            try { foreach (var f in Directory.GetFiles(d)) queue.Enqueue(f); }
            catch (Exception ex) { _log.LogDebug("扫描目录失败 {Dir}: {Error}", d, ex.Message); }
        }

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            var real = ResolveRealPath(path);
            if (real is null || !visited.Add(real)) continue;
            result.Add(real);
            string[] lines;
            try { lines = File.ReadAllLines(real); }
            catch (Exception ex) { _log.LogDebug("无法读取 {File}: {Error}", real, ex.Message); continue; }
            foreach (var line in lines)
            {
                var m = IncludeRe.Match(NginxConfigParser.StripComment(line));
                if (!m.Success) continue;
                var pattern = m.Groups[1].Value.Trim();
                foreach (var p in ExpandIncludePattern(pattern, Path.GetDirectoryName(real) ?? "/etc/apache2"))
                    queue.Enqueue(p);
            }
        }
        return result;
    }

    public async Task<List<ApacheVirtualHost>> LoadVirtualHostsAsync(CancellationToken ct)
    {
        var vhosts = new List<ApacheVirtualHost>();
        foreach (var file in DiscoverConfigFiles())
        {
            ct.ThrowIfCancellationRequested();
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (Exception ex) { _log.LogDebug("无法读取 {File}: {Error}", file, ex.Message); continue; }
            vhosts.AddRange(ParseVirtualHosts(file, lines));
        }
        return vhosts;
    }

    /// <summary>解析文件中的全部 VirtualHost 块</summary>
    public static List<ApacheVirtualHost> ParseVirtualHosts(string filePath, string[] lines)
    {
        var vhosts = new List<ApacheVirtualHost>();
        var text = new List<string>();
        var startLine = 0;
        var inVh = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = NginxConfigParser.StripComment(raw).Trim();
            if (!inVh)
            {
                var m = VhStartRe.Match(trimmed);
                if (m.Success)
                {
                    inVh = true;
                    startLine = i + 1;
                    text.Clear();
                    text.Add(raw);
                }
                continue;
            }
            text.Add(raw);
            if (VhEndRe.IsMatch(trimmed))
            {
                vhosts.Add(BuildVhost(filePath, startLine, i + 1, string.Join("\n", text)));
                inVh = false;
            }
        }
        return vhosts;
    }

    private static ApacheVirtualHost BuildVhost(string filePath, int startLine, int endLine, string text)
    {
        var vh = new ApacheVirtualHost
        {
            FilePath = filePath,
            StartLine = startLine,
            EndLine = endLine,
            Text = text
        };
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var clean = NginxConfigParser.StripComment(raw).Trim();
            if (i == 0)
            {
                vh.Indent = Regex.Match(raw, @"^\s*").Value;
                var m = VhStartRe.Match(clean);
                if (m.Success)
                    foreach (var addr in m.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var li = ParseAddress(addr);
                        if (li is not null) vh.Addresses.Add(li);
                    }
                continue; // 块首行 <VirtualHost ...>
            }
            if (VhEndRe.IsMatch(clean)) continue; // 块末行
            if (clean.Length == 0) continue;

            foreach (Match sm in ServerNameRe.Matches(clean))
            {
                var n = CleanHost(sm.Groups[1].Value);
                if (n.Length > 0 && n != "localhost" && n != "_default_") vh.ServerNames.Add(n);
            }
            foreach (Match sm in ServerAliasRe.Matches(clean))
            {
                foreach (var alias in sm.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var n = CleanHost(alias.Trim());
                    if (n.Length > 0 && n != "localhost" && n != "_default_") vh.ServerNames.Add(n);
                }
            }
            foreach (Match m in SslCertFileRe.Matches(clean)) vh.SslCertificateFiles.Add(m.Groups[1].Value.Trim());
            foreach (Match m in SslCertKeyRe.Matches(clean)) vh.SslCertificateKeyFiles.Add(m.Groups[1].Value.Trim());
            var eng = SslEngineRe.Match(clean);
            if (eng.Success && eng.Groups[1].Value.Equals("on", StringComparison.OrdinalIgnoreCase)) vh.HasSslEngine = true;

            if (ContentDirectives.Any(d => DirectiveStartsWith(clean, d))) vh.HasContent = true;
            if (RedirectDirectives.Any(d => DirectiveStartsWith(clean, d))) vh.HasRedirect = true;
        }
        return vh;
    }

    private static bool DirectiveStartsWith(string line, string directive)
        => line.StartsWith(directive, StringComparison.OrdinalIgnoreCase)
           && (line.Length == directive.Length || char.IsWhiteSpace(line[directive.Length]) || line[directive.Length] == '<');

    /// <summary>解析 &lt;VirtualHost 地址&gt; 参数（*:80 / IP:443 / [::]:80 / _default_:443 / *）</summary>
    private static ApacheListenInfo? ParseAddress(string raw)
    {
        var info = new ApacheListenInfo { Raw = raw };
        var s = raw.Trim();
        if (s.StartsWith("[")) // [::]:80
        {
            var close = s.IndexOf(']');
            if (close < 0) return null;
            info.Address = s[..(close + 1)];
            var rest = s[(close + 1)..];
            info.Port = rest.StartsWith(":") && int.TryParse(rest[1..], out var p) ? p : 80;
        }
        else if (s.Contains(':')) // *:80 / IP:443 / _default_:443
        {
            var idx = s.LastIndexOf(':');
            info.Address = s[..idx];
            info.Port = int.TryParse(s[(idx + 1)..], out var p) ? p : 80;
        }
        else
        {
            info.Address = s;
            info.Port = 80; // 无端口默认 80
        }
        return info;
    }

    /// <summary>收集全部配置文件中的 Listen 端口（创建 443 VirtualHost 前检查是否已监听 443）</summary>
    public HashSet<int> FindListenPorts()
    {
        var ports = new HashSet<int>();
        foreach (var file in DiscoverConfigFiles())
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }
            foreach (var line in lines)
            {
                var m = ListenRe.Match(NginxConfigParser.StripComment(line).Trim());
                if (!m.Success) continue;
                var port = ParsePort(m.Groups[1].Value);
                if (port.HasValue) ports.Add(port.Value);
            }
        }
        return ports;
    }

    private static int? ParsePort(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("["))
        {
            var close = t.IndexOf(']');
            if (close < 0) return null;
            t = t[(close + 1)..];
        }
        var colon = t.LastIndexOf(':');
        var portStr = colon >= 0 ? t[(colon + 1)..] : t;
        return int.TryParse(portStr, out var p) ? p : null;
    }

    /// <summary>块内任意 ServerName/ServerAlias 与证书域名匹配（精确 + 通配双向）</summary>
    public static bool MatchesDomain(ApacheVirtualHost vh, string domain)
        => vh.ServerNames.Any(n => NginxConfigParser.DomainMatch(n, domain));

    /// <summary>清理主机名：剥离开头协议、末尾路径与端口；通配符写法保留</summary>
    public static string CleanHost(string raw)
    {
        var host = raw.Trim();
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) host = host["http://".Length..];
        else if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) host = host["https://".Length..];
        var slash = host.IndexOf('/');
        if (slash > 0) host = host[..slash];
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host[..colon].Contains('.'))
        {
            var port = host[(colon + 1)..];
            if (port.Length > 0 && port.All(char.IsAsciiDigit))
                host = host[..colon];
        }
        return host.Trim();
    }

    private static string? ResolveRealPath(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? path;
        }
        catch { return path; }
    }

    private static IEnumerable<string> ExpandIncludePattern(string pattern, string baseDir)
    {
        string abs;
        if (Path.IsPathRooted(pattern)) abs = pattern;
        else
        {
            var candidates = new[]
            {
                Path.Combine(baseDir, pattern),
                Path.Combine("/etc/apache2", pattern),
                Path.Combine("/etc/httpd", pattern)
            };
            abs = candidates.FirstOrDefault(GlobExists) ?? candidates[0];
        }
        if (abs.Contains('*') || abs.Contains('?'))
        {
            var dir = Path.GetDirectoryName(abs);
            if (string.IsNullOrEmpty(dir)) yield break;
            string[] files;
            try
            {
                if (!Directory.Exists(dir)) yield break;
                files = Directory.GetFiles(dir, Path.GetFileName(abs));
            }
            catch { yield break; }
            foreach (var f in files) yield return f;
        }
        else yield return abs;
    }

    private static bool GlobExists(string p)
    {
        if (!p.Contains('*') && !p.Contains('?')) return File.Exists(p);
        var d = Path.GetDirectoryName(p);
        if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) return false;
        return Directory.GetFiles(d, Path.GetFileName(p)).Length > 0;
    }
}
