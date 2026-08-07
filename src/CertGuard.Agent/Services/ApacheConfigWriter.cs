// ============================================================
// Services/ApacheConfigWriter.cs — Apache 配置改写与回滚
// 职责：SSLCertificateFile / SSLCertificateKeyFile 路径替换、
//       443 VirtualHost 生成（含自动补 Listen 443）、备份、
//       原子写、configtest 失败后自动恢复（回滚）
// ============================================================

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CertGuard.Agent.Services;

public sealed class ApacheConfigWriter
{
    private readonly ILogger _log;
    private readonly Dictionary<string, string[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _originals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _createdKeys = new(StringComparer.Ordinal);
    private string? _backupDir;

    /// <summary>备份根目录（默认与证书目录同级；RedHat 系 /etc/httpd/ssl，Debian 系 /etc/apache2/ssl）</summary>
    public string BackupBase { get; set; } =
        Directory.Exists("/etc/httpd") ? "/etc/httpd/ssl/.backup" : "/etc/apache2/ssl/.backup";
    public int KeepBackups { get; set; } = 5;

    public ApacheConfigWriter(ILogger log) => _log = log;

    public bool HasChanges => _dirty.Count > 0;

    // ── 修改操作 ────────────────────────────────────────────

    /// <summary>替换块内全部 SSLCertificateFile / SSLCertificateKeyFile 路径（仅限该块行范围）</summary>
    public void ReplaceCertificate(ApacheVirtualHost vh, string certPath, string keyPath)
    {
        var lines = GetLines(vh.FilePath);
        var changed = false;
        var start = Math.Max(0, vh.StartLine - 1);
        var end = Math.Min(lines.Length, vh.EndLine);
        for (var i = start; i < end; i++)
        {
            var raw = lines[i];
            var clean = NginxConfigParser.StripComment(raw);
            var m = Regex.Match(clean, @"^(\s*SSLCertificateFile\s+)(\S+)(.*)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                lines[i] = ReplaceValuePreserveComment(raw, m, certPath);
                changed = true;
                continue;
            }
            var mk = Regex.Match(clean, @"^(\s*SSLCertificateKeyFile\s+)(\S+)(.*)$", RegexOptions.IgnoreCase);
            if (mk.Success)
            {
                lines[i] = ReplaceValuePreserveComment(raw, mk, keyPath);
                changed = true;
            }
        }
        if (changed)
        {
            _dirty.Add(vh.FilePath);
            _log.LogInformation("已替换证书路径: {File}:{Start}-{End}", vh.FilePath, vh.StartLine, vh.EndLine);
        }
    }

    /// <summary>基于 80 模板块生成 443 VirtualHost 追加到文件末尾。
    /// ensureListen443 为 true 时先补一行 Listen 443（Apache 的 VirtualHost 端口需要全局 Listen 支持）。
    /// 返回 true 表示本次实际创建；同一模板块被多个域名命中时只创建一次（返回 false）。</summary>
    public bool AddHttpsVirtualHost(ApacheVirtualHost template, string certPath, string keyPath, bool ensureListen443)
    {
        var key = template.FilePath + "|" + template.StartLine;
        if (_createdKeys.Contains(key)) return false;

        var fileLines = GetLines(template.FilePath);
        var tpl = fileLines.Skip(template.StartLine - 1).Take(template.EndLine - template.StartLine + 1).ToList();
        var newBlock = BuildHttpsVhost(tpl, certPath, keyPath);

        var append = new List<string>();
        if (ensureListen443) append.AddRange(new[] { "", "Listen 443" });
        append.Add("");
        append.AddRange(newBlock);
        _files[template.FilePath] = fileLines.Concat(append).ToArray();
        _dirty.Add(template.FilePath);
        _createdKeys.Add(key);
        _log.LogInformation("已生成 443 VirtualHost（模板 {File}:{Line}，补 Listen 443={Ensure}）",
            template.FilePath, template.StartLine, ensureListen443);
        return true;
    }

    // ── 应用与回滚 ──────────────────────────────────────────

    /// <summary>把全部修改写入磁盘（先备份）。返回 (成功, 错误信息)。</summary>
    public async Task<(bool ok, string? error)> ApplyAllAsync(CancellationToken ct)
    {
        if (_dirty.Count == 0) return (true, null);
        try
        {
            Directory.CreateDirectory(BackupBase);
            _backupDir = Path.Combine(BackupBase, DateTime.Now.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(_backupDir);
            CleanupOldBackups();
        }
        catch (Exception ex) { return (false, "创建备份目录失败: " + ex.Message); }

        foreach (var path in _dirty)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!_originals.ContainsKey(path)) _originals[path] = File.ReadAllText(path);
                var bak = Path.Combine(_backupDir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..6] + ".bak");
                File.Copy(path, bak, true);
                var tmp = path + ".certguard.tmp";
                await File.WriteAllTextAsync(tmp, string.Join("\n", _files[path]), ct);
                PreserveFileMode(path, tmp);
                File.Move(tmp, path, true);
                _log.LogInformation("Apache 配置已写入: {Path}（备份 {Bak}）", path, bak);
            }
            catch (Exception ex) { return (false, "写入失败 " + path + ": " + ex.Message); }
        }
        return (true, null);
    }

    /// <summary>从内存原文恢复全部被修改文件（不受取消信号影响），返回是否全部恢复成功</summary>
    public async Task<bool> RollbackAsync()
    {
        var allOk = true;
        foreach (var kv in _originals)
        {
            try
            {
                var tmp = kv.Key + ".certguard.rollback.tmp";
                await File.WriteAllTextAsync(tmp, kv.Value, CancellationToken.None);
                PreserveFileMode(kv.Key, tmp);
                File.Move(tmp, kv.Key, true);
                _log.LogWarning("已回滚 Apache 配置: {Path}", kv.Key);
            }
            catch (Exception ex)
            {
                allOk = false;
                _log.LogError("回滚失败 {Path}: {Error}", kv.Key, ex.Message);
            }
        }
        return allOk;
    }

    // ── 内部实现 ────────────────────────────────────────────

    private string[] GetLines(string path)
    {
        if (!_files.TryGetValue(path, out var lines))
        {
            lines = File.ReadAllLines(path);
            _files[path] = lines;
        }
        return lines;
    }

    /// <summary>替换指令值并保留行尾注释：原行前缀 + 新值 + 原行 # 之后部分</summary>
    private static string ReplaceValuePreserveComment(string rawLine, Match m, string newValue)
    {
        var prefixEnd = Math.Min(m.Groups[1].Index + m.Groups[1].Length, rawLine.Length);
        var prefix = rawLine[..prefixEnd];
        var comment = ExtractTrailingComment(rawLine);
        return prefix + newValue + comment;
    }

    private static string ExtractTrailingComment(string rawLine)
    {
        for (var i = 0; i < rawLine.Length; i++)
            if (rawLine[i] == '#' && (i == 0 || char.IsWhiteSpace(rawLine[i - 1])))
                return rawLine[i..];
        return "";
    }

    /// <summary>把 80 VirtualHost 模板转换为 443：首行地址端口转 443（保留第一个地址）、
    /// 其余指令原样复制、ServerName 后插入 SSLEngine on + 证书两行。</summary>
    internal static List<string> BuildHttpsVhost(List<string> tpl, string certPath, string keyPath)
    {
        var outLines = new List<string>();
        var inserted = false;
        // 块内指令的标准缩进：块首缩进 + 4 空格（与模板内部指令保持一致）
        var inner = (tpl.Count > 0 ? Regex.Match(tpl[0], @"^\s*").Value : "") + "    ";

        for (var i = 0; i < tpl.Count; i++)
        {
            var raw = tpl[i];
            if (i == 0)
            {
                outLines.Add(ConvertFirstAddressTo443(raw));
                continue;
            }
            var clean = NginxConfigParser.StripComment(raw).Trim();
            if (clean.Length == 0) { outLines.Add(raw); continue; }
            var endM = Regex.Match(clean, @"^</VirtualHost\s*>", RegexOptions.IgnoreCase);
            if (endM.Success)
            {
                if (!inserted)
                {
                    outLines.Add($"{inner}SSLEngine on");
                    outLines.Add($"{inner}SSLCertificateFile {certPath}");
                    outLines.Add($"{inner}SSLCertificateKeyFile {keyPath}");
                    inserted = true;
                }
                outLines.Add(raw);
                continue;
            }
            var nm = Regex.Match(clean, @"^(ServerName|ServerAlias)\s+", RegexOptions.IgnoreCase);
            if (nm.Success)
            {
                outLines.Add(raw);
                if (!inserted)
                {
                    outLines.Add($"{inner}SSLEngine on");
                    outLines.Add($"{inner}SSLCertificateFile {certPath}");
                    outLines.Add($"{inner}SSLCertificateKeyFile {keyPath}");
                    inserted = true;
                }
                continue;
            }
            outLines.Add(raw);
        }

        if (!inserted)
        {
            outLines.Add($"{inner}SSLEngine on");
            outLines.Add($"{inner}SSLCertificateFile {certPath}");
            outLines.Add($"{inner}SSLCertificateKeyFile {keyPath}");
        }
        return outLines;
    }

    /// <summary>VirtualHost 首行：保留第一个地址并把端口统一转 443（丢弃多地址中的其余项）</summary>
    internal static string ConvertFirstAddressTo443(string rawFirstLine)
    {
        var m = Regex.Match(rawFirstLine, @"^(?<pre>.*<VirtualHost\s+)(?<addr>[^\s>]+)(?<rest>[^>]*)>$", RegexOptions.IgnoreCase);
        if (!m.Success) return rawFirstLine;
        return m.Groups["pre"].Value + ConvertAddrPort(m.Groups["addr"].Value) + ">";
    }

    private static string ConvertAddrPort(string addr)
    {
        var s = addr.Trim();
        if (s.StartsWith("[")) // [::]:80
        {
            var close = s.IndexOf(']');
            if (close < 0) return s;
            return s[..(close + 1)] + ":443";
        }
        if (s.Contains(':')) // *:80 / IP:443 / _default_:443
        {
            var idx = s.LastIndexOf(':');
            return s[..idx] + ":443";
        }
        return s + ":443"; // 裸地址无端口 → 补 :443
    }

    private static void PreserveFileMode(string source, string tmp)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(source);
            File.SetUnixFileMode(tmp, mode);
        }
        catch { /* 保持默认模式 */ }
    }

    private void CleanupOldBackups()
    {
        try
        {
            var dirs = Directory.GetDirectories(BackupBase)
                .Where(d => DateTime.TryParseExact(Path.GetFileName(d), "yyyyMMddHHmmss", null,
                    System.Globalization.DateTimeStyles.None, out _))
                .OrderByDescending(d => d)
                .ToList();
            foreach (var d in dirs.Skip(KeepBackups))
            {
                try { Directory.Delete(d, true); }
                catch (Exception ex) { _log.LogDebug("清理旧备份失败 {Dir}: {Error}", d, ex.Message); }
            }
        }
        catch (Exception ex) { _log.LogDebug("清理旧备份失败: {Error}", ex.Message); }
    }
}
