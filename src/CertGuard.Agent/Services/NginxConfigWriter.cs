// ============================================================
// Services/NginxConfigWriter.cs — Nginx 配置改写与回滚
// 职责：证书路径替换（行级）、443 块生成、备份、原子写、
//       nginx -t 失败后自动恢复（回滚）
// ============================================================

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CertGuard.Agent.Services;

public sealed class NginxConfigWriter
{
    private readonly ILogger _log;
    /// <summary>文件 → 内存态行数组（所有修改基于同一数组，保证多块修改一致）</summary>
    private readonly Dictionary<string, string[]> _files = new(StringComparer.Ordinal);
    /// <summary>被修改的文件集合</summary>
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    /// <summary>文件 → 磁盘原文（用于回滚）</summary>
    private readonly Dictionary<string, string> _originals = new(StringComparer.Ordinal);
    /// <summary>已创建 443 块的模板（file|startLine），用于 SAN 多域名命中同一块时去重</summary>
    private readonly HashSet<string> _createdKeys = new(StringComparer.Ordinal);
    private string? _backupDir;

    /// <summary>备份根目录（默认与证书目录同级的隐藏目录）</summary>
    public string BackupBase { get; set; } = "/etc/nginx/ssl/.backup";
    /// <summary>保留最近备份份数</summary>
    public int KeepBackups { get; set; } = 5;

    public NginxConfigWriter(ILogger log) => _log = log;

    public bool HasChanges => _dirty.Count > 0;

    // ── 修改操作 ────────────────────────────────────────────

    /// <summary>替换块内全部 ssl_certificate / ssl_certificate_key 路径（仅限该块行范围）</summary>
    public void ReplaceCertificate(NginxServerBlock block, string certPath, string keyPath)
    {
        var lines = GetLines(block.FilePath);
        var changed = false;
        var start = Math.Max(0, block.StartLine - 1);
        var end = Math.Min(lines.Length, block.EndLine);
        for (var i = start; i < end; i++)
        {
            var raw = lines[i];
            var clean = NginxConfigParser.StripComment(raw);
            var m = Regex.Match(clean, @"^(\s*ssl_certificate\s+)([^;]+);", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var prefix = raw[..Math.Min(m.Groups[1].Index + m.Groups[1].Length, raw.Length)];
                // 注意保留分号结尾，否则 nginx 会把下一行误解析为指令参数
                lines[i] = prefix + certPath + ";" + ExtractTrailingComment(raw);
                changed = true;
                continue;
            }
            var mk = Regex.Match(clean, @"^(\s*ssl_certificate_key\s+)([^;]+);", RegexOptions.IgnoreCase);
            if (mk.Success)
            {
                var prefix = raw[..Math.Min(mk.Groups[1].Index + mk.Groups[1].Length, raw.Length)];
                lines[i] = prefix + keyPath + ";" + ExtractTrailingComment(raw);
                changed = true;
            }
        }
        if (changed)
        {
            _dirty.Add(block.FilePath);
            _log.LogInformation("已替换证书路径: {File}:{Start}-{End}", block.FilePath, block.StartLine, block.EndLine);
        }
    }

    /// <summary>基于 80 模板块生成 443 块并追加到文件末尾。
    /// 返回 true 表示本次实际创建；同一模板块被多个域名命中时只创建一次（返回 false）。</summary>
    public bool AddHttpsServer(NginxServerBlock template, string certPath, string keyPath)
    {
        var key = template.FilePath + "|" + template.StartLine;
        if (_createdKeys.Contains(key)) return false;

        var fileLines = GetLines(template.FilePath);
        var tpl = fileLines.Skip(template.StartLine - 1).Take(template.EndLine - template.StartLine + 1).ToList();
        var newBlock = BuildHttpsBlock(tpl, certPath, keyPath);

        _files[template.FilePath] = fileLines.Concat(new[] { "" }).Concat(newBlock).ToArray();
        _dirty.Add(template.FilePath);
        _createdKeys.Add(key);
        _log.LogInformation("已生成 443 配置（模板 {File}:{Line}）", template.FilePath, template.StartLine);
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
        catch (Exception ex)
        {
            return (false, "创建备份目录失败: " + ex.Message);
        }

        foreach (var path in _dirty)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!_originals.ContainsKey(path))
                    _originals[path] = File.ReadAllText(path);

                var bak = Path.Combine(_backupDir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..6] + ".bak");
                File.Copy(path, bak, true);

                var tmp = path + ".certguard.tmp";
                await File.WriteAllTextAsync(tmp, string.Join("\n", _files[path]), ct);
                PreserveFileMode(path, tmp); // 保留原文件权限/属主语义，避免 umask 覆盖特殊权限
                File.Move(tmp, path, true);
                _log.LogInformation("Nginx 配置已写入: {Path}（备份 {Bak}）", path, bak);
            }
            catch (Exception ex)
            {
                return (false, "写入失败 " + path + ": " + ex.Message);
            }
        }
        return (true, null);
    }

    /// <summary>从内存原文恢复全部被修改文件。
    /// 使用 CancellationToken.None 保证回滚不受服务停止等取消信号影响，
    /// 避免回滚写一半导致配置半改状态。返回是否全部恢复成功。</summary>
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
                _log.LogWarning("已回滚 Nginx 配置: {Path}", kv.Key);
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

    /// <summary>把 80 模板块转换为 443 块：listen 转 443 ssl（去 default_server，
    /// IPv6 形态保留）、server_name 原样、其余指令原样复制、插入证书两行。</summary>
    private static List<string> BuildHttpsBlock(List<string> tpl, string certPath, string keyPath)
    {
        var outLines = new List<string>();
        var indent = "";
        var inserted = false;

        for (var i = 0; i < tpl.Count; i++)
        {
            var raw = tpl[i];
            if (i == 0)
            {
                indent = Regex.Match(raw, @"^\s*").Value;
                outLines.Add(raw); // "server {"
                continue;
            }
            var clean = NginxConfigParser.StripComment(raw).Trim();
            if (clean.Length == 0) { outLines.Add(raw); continue; }

            var lm = Regex.Match(clean, @"^listen\s+([^;]+);", RegexOptions.IgnoreCase);
            if (lm.Success)
            {
                outLines.Add(ReplaceValueInLine(raw, ConvertToHttpsListen(lm.Groups[1].Value.Trim())));
                continue;
            }
            var sm = Regex.Match(clean, @"^server_name\s+", RegexOptions.IgnoreCase);
            if (sm.Success)
            {
                outLines.Add(raw);
                if (!inserted)
                {
                    var inner = InnerIndent(indent);
                    outLines.Add($"{inner}ssl_certificate     {certPath};");
                    outLines.Add($"{inner}ssl_certificate_key {keyPath};");
                    inserted = true;
                }
                continue;
            }
            outLines.Add(raw);
        }

        if (!inserted)
        {
            var inner = InnerIndent(indent);
            var closeIdx = outLines.FindLastIndex(l => l.Trim() == "}");
            if (closeIdx >= 0)
            {
                outLines.Insert(closeIdx, $"{inner}ssl_certificate     {certPath};");
                outLines.Insert(closeIdx + 1, $"{inner}ssl_certificate_key {keyPath};");
            }
            else
            {
                outLines.Add($"{inner}ssl_certificate     {certPath};");
                outLines.Add($"{inner}ssl_certificate_key {keyPath};");
            }
        }
        return outLines;
    }

    private static string InnerIndent(string indent)
        => indent.Length >= 4 ? indent + "    " : "    ";

    /// <summary>listen 指令值转 https：端口统一为 443、加 ssl、去掉 default_server、保留其余参数</summary>
    private static string ConvertToHttpsListen(string listenRaw)
    {
        var parts = listenRaw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "443 ssl";
        var addrPort = parts[0];
        var others = new List<string>();
        var hasSsl = false;
        foreach (var p in parts.Skip(1))
        {
            if (p.Equals("ssl", StringComparison.OrdinalIgnoreCase)) { hasSsl = true; continue; }
            if (p.Equals("default_server", StringComparison.OrdinalIgnoreCase)
                || p.Equals("default", StringComparison.OrdinalIgnoreCase)) continue;
            others.Add(p);
        }

        string newAddrPort;
        if (addrPort.StartsWith("[")) // [::]:80 / [::]
        {
            var close = addrPort.IndexOf(']');
            if (close < 0) newAddrPort = addrPort;
            else
            {
                var rest = addrPort[(close + 1)..];
                newAddrPort = addrPort[..(close + 1)] + (rest.StartsWith(":") ? ":443" : rest.Length == 0 ? ":443" : rest);
            }
        }
        else if (addrPort.Contains(':')) // *:80 / 0.0.0.0:80 / 127.0.0.1:8080
        {
            var idx = addrPort.LastIndexOf(':');
            newAddrPort = addrPort[..idx] + ":443";
        }
        else if (addrPort == "80" || int.TryParse(addrPort, out _))
        {
            newAddrPort = "443"; // 任意 http 端口统一转 443
        }
        else newAddrPort = addrPort;

        var result = new List<string> { newAddrPort };
        result.AddRange(others);
        if (!hasSsl) result.Add("ssl");
        return string.Join(" ", result);
    }

    /// <summary>替换行内 listen 指令的值（保留缩进与行尾注释）</summary>
    private static string ReplaceValueInLine(string rawLine, string newValue)
    {
        var m = Regex.Match(rawLine, @"^(\s*listen\s+)([^;]+)(;.*)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return rawLine;
        return m.Groups[1].Value + newValue + (m.Groups[3].Success ? m.Groups[3].Value : ";");
    }

    /// <summary>提取并保留行尾注释（含 # 本身，前面已带空格）</summary>
    private static string ExtractTrailingComment(string rawLine)
    {
        for (var i = 0; i < rawLine.Length; i++)
        {
            if (rawLine[i] == '#' && (i == 0 || char.IsWhiteSpace(rawLine[i - 1])))
                return rawLine[i..];
        }
        return "";
    }

    /// <summary>Linux 下把原文件的权限模式复制到临时文件，避免原子替换后权限漂移</summary>
    private static void PreserveFileMode(string source, string tmp)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(source);
            File.SetUnixFileMode(tmp, mode);
        }
        catch { /* 保持默认模式即可 */ }
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
