// ============================================================
// Services/NginxConfigParser.cs — Nginx 配置解析层
// 职责：配置文件发现（include 递归展开 + glob）、server 块级
//       解析（括号深度 + 注释剥离）、listen / server_name /
//       ssl 指令提取、域名匹配（精确 + 通配，双向）
// ============================================================

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CertGuard.Agent.Services;

/// <summary>解析后的 listen 指令</summary>
public sealed class NginxListen
{
    /// <summary>监听地址："" / "*" / "0.0.0.0" / "[::]" / "127.0.0.1" 等</summary>
    public string Address { get; set; } = "";
    /// <summary>监听端口（unix socket 为 -1）</summary>
    public int Port { get; set; }
    /// <summary>是否带 ssl 参数</summary>
    public bool Ssl { get; set; }
    /// <summary>是否 default_server（兼容老语法 default）</summary>
    public bool DefaultServer { get; set; }
    /// <summary>原始值</summary>
    public string Raw { get; set; } = "";
}

/// <summary>解析后的 server 块（含行号定位，用于精确改写）</summary>
public sealed class NginxServerBlock
{
    /// <summary>真实文件路径（sites-enabled 符号链接已解析）</summary>
    public string FilePath { get; set; } = "";
    /// <summary>块起始行号（1-based，含 "server {" 行）</summary>
    public int StartLine { get; set; }
    /// <summary>块结束行号（1-based，含结束的 "}"）</summary>
    public int EndLine { get; set; }
    /// <summary>块原文（含注释），供行级改写</summary>
    public string Text { get; set; } = "";
    /// <summary>块首行的行首缩进</summary>
    public string Indent { get; set; } = "";
    public List<string> ServerNames { get; set; } = new();
    public List<NginxListen> Listens { get; set; } = new();
    public List<string> SslCertificateLines { get; set; } = new();
    public List<string> SslCertificateKeyLines { get; set; } = new();
    /// <summary>老式 ssl on; 指令</summary>
    public bool HasSslOn { get; set; }
    /// <summary>含 return / rewrite 指令</summary>
    public bool HasRedirect { get; set; }

    /// <summary>块内是否已配置证书（ssl_certificate 指令，任何端口）</summary>
    public bool HasSslCertificate => SslCertificateLines.Count > 0;
    /// <summary>是否存在带 ssl 参数的 listen</summary>
    public bool HasSslListen => Listens.Any(l => l.Ssl);
    /// <summary>纯重定向块：有跳转指令但无可服务内容</summary>
    public bool IsRedirectOnly => HasRedirect && !HasContent;
    /// <summary>是否有可服务内容（root/alias/proxy_pass/location 等）</summary>
    public bool HasContent { get; set; }
}

/// <summary>Nginx 配置解析器（无状态，线程安全方法）</summary>
public sealed class NginxConfigParser
{
    private readonly ILogger _log;
    private static readonly Regex IncludeRe = new(@"^\s*include\s+([^;]+);", RegexOptions.IgnoreCase);
    private static readonly Regex ServerNameRe = new(@"^\s*server_name\s+([^;]+);", RegexOptions.IgnoreCase);
    private static readonly Regex ListenRe = new(@"^\s*listen\s+([^;]+);", RegexOptions.IgnoreCase);
    private static readonly Regex SslCertRe = new(@"^\s*ssl_certificate\s+(.+?);", RegexOptions.IgnoreCase);
    private static readonly Regex SslCertKeyRe = new(@"^\s*ssl_certificate_key\s+(.+?);", RegexOptions.IgnoreCase);
    private static readonly Regex SslOnRe = new(@"^\s*ssl\s+on\s*;", RegexOptions.IgnoreCase);

    private static readonly string[] ContentDirectives =
    {
        "root", "alias", "proxy_pass", "fastcgi_pass", "uwsgi_pass", "scgi_pass",
        "grpc_pass", "location", "try_files", "index", "content_by_lua", "error_page"
    };
    private static readonly string[] RedirectDirectives = { "return", "rewrite" };

    public NginxConfigParser(ILogger log) => _log = log;

    /// <summary>主配置路径（Debian / RedHat 系通用）</summary>
    public static string MainConfigPath { get; set; } = "/etc/nginx/nginx.conf";

    /// <summary>兜底扫描目录（nginx.conf 无法读取时的补充）</summary>
    public static string[] FallbackDirs { get; set; } =
        { "/etc/nginx/conf.d", "/etc/nginx/sites-enabled" };

    // ── 配置发现 ────────────────────────────────────────────

    /// <summary>发现全部真实配置文件（include 递归展开，symlink 解析，去重）</summary>
    public List<string> DiscoverConfigFiles()
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        if (File.Exists(MainConfigPath)) queue.Enqueue(MainConfigPath);
        foreach (var d in FallbackDirs)
        {
            if (!Directory.Exists(d)) continue;
            try
            {
                foreach (var f in Directory.GetFiles(d)) queue.Enqueue(f);
            }
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
                var m = IncludeRe.Match(StripComment(line));
                if (!m.Success) continue;
                var pattern = m.Groups[1].Value.Trim();
                foreach (var p in ExpandIncludePattern(pattern, Path.GetDirectoryName(real) ?? "/etc/nginx"))
                    queue.Enqueue(p);
            }
        }
        return result;
    }

    /// <summary>发现 + 解析全部 server 块</summary>
    public async Task<List<NginxServerBlock>> LoadServerBlocksAsync(CancellationToken ct)
    {
        var blocks = new List<NginxServerBlock>();
        foreach (var file in DiscoverConfigFiles())
        {
            ct.ThrowIfCancellationRequested();
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (Exception ex) { _log.LogDebug("无法读取 {File}: {Error}", file, ex.Message); continue; }
            blocks.AddRange(ParseServerBlocks(file, lines));
        }
        return blocks;
    }

    /// <summary>按括号深度解析文件中的全部 server 块</summary>
    public static List<NginxServerBlock> ParseServerBlocks(string filePath, string[] lines)
    {
        var blocks = new List<NginxServerBlock>();
        var sb = new StringBuilder();
        var startLine = 0;
        var depth = 0;
        var inServer = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = StripComment(raw).Trim();
            if (!inServer)
            {
                // 仅当行首为 "server {" 才进入（排除 server_name / server_tokens 等）
                if (Regex.IsMatch(trimmed, @"^server\s*\{", RegexOptions.IgnoreCase))
                {
                    inServer = true;
                    depth = 0;
                    startLine = i + 1;
                    sb.Clear();
                    sb.AppendLine(raw);
                    depth += CountBraces(trimmed);
                    if (depth <= 0)
                    {
                        blocks.Add(BuildBlock(filePath, startLine, i + 1, sb.ToString()));
                        inServer = false;
                    }
                }
                continue;
            }
            sb.AppendLine(raw);
            depth += CountBraces(trimmed);
            if (depth <= 0)
            {
                blocks.Add(BuildBlock(filePath, startLine, i + 1, sb.ToString()));
                inServer = false;
            }
        }
        return blocks;
    }

    private static NginxServerBlock BuildBlock(string filePath, int startLine, int endLine, string text)
    {
        var block = new NginxServerBlock
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
            if (i == 0)
            {
                block.Indent = Regex.Match(raw, @"^\s*").Value;
                continue; // 块首行 "server {"
            }
            var clean = StripComment(raw).Trim();
            if (clean.Length == 0) continue;

            var m = ServerNameRe.Match(clean);
            if (m.Success)
            {
                block.ServerNames.AddRange(m.Groups[1].Value
                    .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
                continue;
            }
            m = ListenRe.Match(clean);
            if (m.Success)
            {
                var l = ParseListen(m.Groups[1].Value.Trim());
                if (l is not null) block.Listens.Add(l);
                continue;
            }
            m = SslCertRe.Match(clean);
            if (m.Success) { block.SslCertificateLines.Add(m.Groups[1].Value.Trim()); continue; }
            m = SslCertKeyRe.Match(clean);
            if (m.Success) { block.SslCertificateKeyLines.Add(m.Groups[1].Value.Trim()); continue; }
            if (SslOnRe.IsMatch(clean)) { block.HasSslOn = true; continue; }

            if (ContentDirectives.Any(d => DirectiveStartsWith(clean, d))) block.HasContent = true;
            if (RedirectDirectives.Any(d => DirectiveStartsWith(clean, d))) block.HasRedirect = true;
        }
        return block;
    }

    /// <summary>指令前缀匹配（避免 root 匹配 rooter 之类）</summary>
    private static bool DirectiveStartsWith(string line, string directive)
        => line.StartsWith(directive, StringComparison.OrdinalIgnoreCase)
           && (line.Length == directive.Length || char.IsWhiteSpace(line[directive.Length]));

    /// <summary>解析 listen 指令；unix socket / 无法解析返回 null</summary>
    private static NginxListen? ParseListen(string raw)
    {
        var listen = new NginxListen { Raw = raw };
        var parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var addrPort = parts[0];

        foreach (var p in parts.Skip(1))
        {
            if (p.Equals("ssl", StringComparison.OrdinalIgnoreCase)) listen.Ssl = true;
            else if (p.Equals("default_server", StringComparison.OrdinalIgnoreCase)
                     || p.Equals("default", StringComparison.OrdinalIgnoreCase)) listen.DefaultServer = true;
        }

        if (addrPort.StartsWith("unix:")) return null;
        if (addrPort.StartsWith("[")) // [::]:443
        {
            var close = addrPort.IndexOf(']');
            if (close < 0) return null;
            listen.Address = addrPort[..(close + 1)];
            var rest = addrPort[(close + 1)..];
            listen.Port = rest.StartsWith(":") && int.TryParse(rest[1..], out var p) ? p : 80;
        }
        else if (addrPort.Contains(':')) // 0.0.0.0:443 / *:443 / 127.0.0.1:8080
        {
            var idx = addrPort.LastIndexOf(':');
            listen.Address = addrPort[..idx];
            listen.Port = int.TryParse(addrPort[(idx + 1)..], out var p) ? p : 80;
        }
        else if (int.TryParse(addrPort, out var port))
        {
            listen.Port = port;
        }
        else return null;
        return listen;
    }

    // ── 域名匹配（精确 + 通配，双向） ──────────────────────

    /// <summary>块内任意 server_name 与证书域名匹配</summary>
    public static bool MatchesDomain(NginxServerBlock block, string domain)
        => block.ServerNames.Any(n => DomainMatch(n, domain));

    /// <summary>双向匹配：server_name n 与证书域名 d。
    /// 1) 完全相等（忽略大小写）
    /// 2) n 为通配符 *.base 且 d 为 base 的非裸子域
    /// 3) d 为通配符 *.base 且 n 为 base 的非裸子域
    /// 正则 server_name（~ 开头）不处理。</summary>
    public static bool DomainMatch(string n, string d)
    {
        n = n.Trim();
        d = d.Trim();
        if (n.Length == 0 || d.Length == 0 || n.StartsWith('~') || d.StartsWith('~')) return false;
        if (string.Equals(n, d, StringComparison.OrdinalIgnoreCase)) return true;
        return WildcardMatch(n, d) || WildcardMatch(d, n);
    }

    /// <summary>通配符 *.base 是否覆盖 host。
    /// 与 nginx 语义一致：只匹配 base 的**一级**子域（* 不匹配裸域本身，
    /// 也不匹配多级子域 a.b.base）。兼容老语法 .base。</summary>
    private static bool WildcardMatch(string wildcard, string host)
    {
        string baseName;
        if (wildcard.StartsWith("*.", StringComparison.OrdinalIgnoreCase))
        {
            baseName = wildcard[2..];
        }
        else if (wildcard.StartsWith('.') && wildcard.Length > 1 && !wildcard.StartsWith(".."))
        {
            baseName = wildcard[1..]; // nginx 老式通配符语法 .example.com
        }
        else return false;

        if (baseName.Length == 0) return false;
        if (!host.EndsWith("." + baseName, StringComparison.OrdinalIgnoreCase)) return false;
        var sub = host[..(host.Length - baseName.Length - 1)];
        return sub.Length > 0 && !sub.Contains('.');
    }

    // ── 通用工具 ────────────────────────────────────────────

    /// <summary>剥离行尾注释：'#' 前必须有空白或行首，避免误伤 URL 等</summary>
    public static string StripComment(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                return line[..i];
        }
        return line;
    }

    private static int CountBraces(string s)
    {
        var n = 0;
        foreach (var c in s)
        {
            if (c == '{') n++;
            else if (c == '}') n--;
        }
        return n;
    }

    /// <summary>解析符号链接为真实路径（sites-enabled → sites-available）</summary>
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
        var abs = ResolveAbsInclude(pattern, baseDir);
        if (abs is null) yield break;

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
            catch
            {
                yield break; // 目录不可读时不阻塞整体解析
            }
            foreach (var f in files) yield return f;
        }
        else yield return abs;
    }

    /// <summary>解析 include 路径：绝对路径原样；相对路径依次尝试配置文件目录 / /etc/nginx / nginx prefix</summary>
    private static string? ResolveAbsInclude(string pattern, string baseDir)
    {
        if (Path.IsPathRooted(pattern)) return pattern;
        var candidates = new[]
        {
            Path.Combine(baseDir, pattern),
            Path.Combine("/etc/nginx", pattern),
            Path.Combine("/usr/local/nginx/conf", pattern)
        };
        foreach (var c in candidates)
        {
            try { if (GlobExists(c)) return c; }
            catch { /* 权限不足等，尝试下一个候选 */ }
        }
        return candidates[0];
    }

    private static bool GlobExists(string p)
    {
        if (!p.Contains('*') && !p.Contains('?')) return File.Exists(p);
        var d = Path.GetDirectoryName(p);
        if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) return false;
        return Directory.GetFiles(d, Path.GetFileName(p)).Length > 0;
    }
}
