// ============================================================
// Program.cs — Agent 入口
// 用法:
//   首次: certguard-agent --token ct_reg_xxxxxx
//   更新密钥: certguard-agent --update-secret <新密钥>
//   更新版本: certguard-agent --update <下载地址>
//   卸载: certguard-agent --uninstall
//   日常: certguard-agent
// ============================================================

using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using CertGuard.Agent.Models;
using CertGuard.Agent.Services;
using CertGuard.Agent.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// ── 解析参数与配置 ────────────────────────────────────────
var cfg = new AgentConfig();
var showHelp = false;

// 1) 尝试加载配置文件
//    优先顺序（跨平台）:
//      1. 可执行文件同目录下的 agent.json
//      2. 系统数据目录下的 agent.json（%ProgramData%\CertGuard 或 /etc/certguard）
//      3. 当前工作目录下的 agent.json
var exeDir = Path.GetDirectoryName(Environment.GetCommandLineArgs()[0]) ?? ".";
var configPaths = OperatingSystem.IsWindows()
    ? new[] {
        Path.Combine(exeDir, "agent.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CertGuard", "agent.json"),
        "agent.json"
      }
    : new[] {
        Path.Combine(exeDir, "agent.json"),
        "/etc/certguard/agent.json",
        Path.Combine(cfg.DataDir, "agent.json"),
        "agent.json"
      };

foreach (var p in configPaths)
{
    if (!File.Exists(p)) continue;
    try
    {
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
        var r = json.RootElement;
        cfg.AgentId     = r.TryGetPropertyValue("agent_id",     out var a) ? a.GetString() ?? "" : "";
        cfg.AgentSecret = r.TryGetPropertyValue("agent_secret", out var s) ? s.GetString() ?? "" : "";
        cfg.ApiBaseUrl  = r.TryGetPropertyValue("api_base_url", out var u) ? u.GetString() ?? cfg.ApiBaseUrl : cfg.ApiBaseUrl;
        cfg.HeartbeatSec = r.TryGetPropertyValue("heartbeat_sec", out var h) ? h.GetInt32() : 60;
        cfg.DataDir     = r.TryGetPropertyValue("data_dir",     out var d) ? d.GetString() ?? cfg.DataDir : cfg.DataDir;
        break;
    }
    catch { /* ignore corrupt config */ }
}

// 2) 命令行参数
string? updateUrl = null;
var uninstall = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--token":         if (i + 1 < args.Length) cfg.RegisterToken = args[++i]; break;
        case "--server":        if (i + 1 < args.Length) cfg.ApiBaseUrl = args[++i]; break;
        case "--update-secret": if (i + 1 < args.Length) cfg.NewSecret = args[++i]; break;
        case "--update":        if (i + 1 < args.Length) updateUrl = args[++i]; break;
        case "--data-dir":      if (i + 1 < args.Length) cfg.DataDir = args[++i]; break;
        case "--heartbeat":     if (i + 1 < args.Length && int.TryParse(args[++i], out var hb)) cfg.HeartbeatSec = hb; break;
        case "--register-only": cfg.RegisterOnly = true; break;
        case "--insecure":      cfg.AllowInsecure = true; break;
        case "--uninstall":     uninstall = true; break;
        case "--keep-data":     cfg.KeepData = true; break;
        case "--help": case "-h": showHelp = true; break;
        case "--version": case "-v": Console.WriteLine($"certguard-agent v{CertGuard.Agent.Models.AgentInfo.Version}"); return 0;
    }
}

// 3) 环境变量兜底
cfg.RegisterToken ??= Environment.GetEnvironmentVariable("CERTGUARD_TOKEN");
cfg.ApiBaseUrl    = Environment.GetEnvironmentVariable("CERTGUARD_SERVER") ?? cfg.ApiBaseUrl;
if (Environment.GetEnvironmentVariable("CERTGUARD_INSECURE") == "1")
    cfg.AllowInsecure = true;

// ── 帮助 ──────────────────────────────────────────────────
if (showHelp)
{
    Console.WriteLine(@"
CertGuard Agent — SSL certificate auto-deployment daemon

Usage:
  certguard-agent [options]

Options:
  --token <t>      One-time register token (from TOPSSL.CN console)
  --server <url>   Platform API URL (default: http://localhost:5003)
  --update-secret <s>  Update agent secret in local config and exit
  --update <url>       Download and replace agent binary, then exit
  --data-dir <p>   Data directory
  --heartbeat <s>  Heartbeat interval in seconds (default: 60)
  --register-only  Register then exit (used by install script)
  --insecure        Skip SSL certificate validation (for self-signed cert)
  --uninstall       Uninstall agent: stop service, clean PATH, remove files
  --keep-data       Keep data directory (use with --uninstall)
  --version, -v    Show version
  --help, -h       Show this help

First run:
  certguard-agent --token ct_reg_xxxxxx

Update secret:
  certguard-agent --update-secret <new_secret>

Update version:
  certguard-agent --update <download_url>

Uninstall:
  certguard-agent --uninstall [--keep-data]

Env vars:
  CERTGUARD_TOKEN  Register token
  CERTGUARD_SERVER Platform API URL
");
    return 0;
}

// ── --update-secret 模式 ──────────────────────────────
if (!string.IsNullOrEmpty(cfg.NewSecret))
{
    var configFile = configPaths.FirstOrDefault(File.Exists);
    if (configFile == null)
    {
        Console.Error.WriteLine("错误: 未找到 agent.json，请先注册。");
        Console.Error.WriteLine($"      查找路径: {string.Join(", ", configPaths)}");
        return 1;
    }

    try
    {
        var text = File.ReadAllText(configFile);
        var newText = Regex.Replace(
            text,
            "\"agent_secret\"\\s*:\\s*\"[^\"]*\"",
            $"\"agent_secret\": \"{cfg.NewSecret}\""
        );

        File.WriteAllText(configFile, newText);
        Console.WriteLine($"[√] 密钥已更新！配置文件: {configFile}");
        Console.WriteLine("   请重启服务使新密钥生效。");
        Console.WriteLine($"   Windows: Restart-Service TopSSLCertGuardAgent");
        Console.WriteLine($"   Linux:   systemctl restart topssl-certguard-agent");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"错误: 更新配置文件失败 - {ex.Message}");
        return 1;
    }
}

// ── --update 模式 ────────────────────────────────────
if (!string.IsNullOrEmpty(updateUrl))
{
    try
    {
        var exePath = Environment.GetCommandLineArgs()[0];
        var dir = Path.GetDirectoryName(exePath)!;
        var zipPath = Path.Combine(Path.GetTempPath(), $"certguard_update_{Guid.NewGuid():N}.zip");

        Console.WriteLine("正在下载更新包...");
        Console.WriteLine($"  {updateUrl}");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var bytes = await http.GetByteArrayAsync(updateUrl);
        await File.WriteAllBytesAsync(zipPath, bytes);

        Console.WriteLine($"下载完成 ({bytes.Length / 1024.0:F1} KB)");
        Console.WriteLine("正在解压更新...");

        // 备份当前版本（排除 agent.json）
        var backupDir = dir + $"_{DateTime.Now:yyyyMMddHHmmss}";
        Console.WriteLine($"  备份旧版本到: {backupDir}");
        Directory.CreateDirectory(backupDir);

        foreach (var f in Directory.GetFiles(dir))
        {
            var name = Path.GetFileName(f);
            if (name.Equals("agent.json", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Move(f, Path.Combine(backupDir, name));
        }

        // 解压新版本
        ZipFile.ExtractToDirectory(zipPath, dir);

        // 恢复配置文件（如果被覆盖）
        var newConfig = Path.Combine(dir, "agent.json");
        if (!File.Exists(newConfig))
        {
            var oldConfig = Path.Combine(backupDir, "agent.json");
            if (File.Exists(oldConfig))
                File.Copy(oldConfig, newConfig);
        }

        // 确保 Linux 上可执行
        if (!OperatingSystem.IsWindows())
        {
            var agentFile = Path.Combine(dir, "certguard-agent");
            if (File.Exists(agentFile))
                File.SetUnixFileMode(agentFile,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        // 清理
        try { File.Delete(zipPath); } catch { }

        Console.WriteLine($"[√] 更新完成！版本已升级到 {dir}");
        Console.WriteLine("   旧版本备份在: " + backupDir);
        Console.WriteLine("   请重启服务使新版本生效。");
        Console.WriteLine($"   Windows: Restart-Service TopSSLCertGuardAgent");
        Console.WriteLine($"   Linux:   systemctl restart topssl-certguard-agent");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"错误: 更新失败 - {ex.Message}");
        return 1;
    }
}

// ── --uninstall 模式 ────────────────────────────
if (uninstall)
{
    return UninstallAgent(cfg, exeDir, configPaths);
}

// ── 配置 Serilog 日志文件 ──────────────────────────
var logDir = Path.Combine(cfg.DataDir, "logs");
try { Directory.CreateDirectory(logDir); } catch { }

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(logDir, "certguard-agent-.log"),
        rollingInterval: Serilog.RollingInterval.Day,
        retainedFileCountLimit: 7,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true
    )
    .CreateLogger();

var prevLogDir = logDir;
// ── 注册后配置写入路径 ────────────────────────────────────
var exeDirConfig = Path.Combine(exeDir, "agent.json");
cfg.ConfigWritePath = File.Exists(exeDirConfig)
    ? exeDirConfig
    : null;

// ── 构建 & 启动 ───────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o =>
{
    o.ServiceName = "TopSSLCertGuardAgent";
});

builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

builder.Services.Configure<AgentConfig>(o =>
{
    o.ApiBaseUrl    = cfg.ApiBaseUrl.TrimEnd('/');
    o.AgentId       = cfg.AgentId;
    o.AgentSecret   = cfg.AgentSecret;
    o.RegisterToken = cfg.RegisterToken;
    o.HeartbeatSec  = cfg.HeartbeatSec;
    o.DataDir       = cfg.DataDir;
    o.RegisterOnly  = cfg.RegisterOnly;
    o.AllowInsecure  = cfg.AllowInsecure;
    o.ConfigWritePath = cfg.ConfigWritePath;
});

var apiHost = new Uri(cfg.ApiBaseUrl.TrimEnd('/'));

builder.Services.AddHttpClient<PlatformClient>(c =>
{
    c.BaseAddress = apiHost;
    c.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler();

    if (cfg.AllowInsecure || IsLocalhost(apiHost))
    {
        handler.ServerCertificateCustomValidationCallback =
            (_, _, _, _) => true;
    }

    return handler;
});

builder.Services.AddSingleton<ProviderFactory>();
builder.Services.AddHostedService<AgentWorker>();

try
    {
        await builder.Build().RunAsync();
        return 0;
    }
    finally
    {
        Log.CloseAndFlush();
    }


static int UninstallAgent(AgentConfig cfg, string exeDir, string[] configPaths)
{
    var serviceName = OperatingSystem.IsWindows() ? "TopSSLCertGuardAgent" : "topssl-certguard-agent";
    var installDir = exeDir;
    var dataDir = cfg.DataDir;
    var exePath = Environment.GetCommandLineArgs()[0];
    Console.WriteLine("================================================");
    Console.WriteLine("  CertGuard Agent 卸载程序");
    Console.WriteLine("================================================");
    Console.WriteLine();
    var configFile = configPaths.FirstOrDefault(File.Exists);
    if (configFile != null)
    {
        try
        {
            var json = JsonDocument.Parse(File.ReadAllText(configFile));
            if (json.RootElement.TryGetProperty("data_dir", out var dd))
                dataDir = dd.GetString() ?? dataDir;
        }
        catch { }
    }
    Console.Write("  [1/4] 停止并删除服务... ");
    try
    {
        if (OperatingSystem.IsWindows())
        {
            using var stopProc = Process.Start(new ProcessStartInfo("sc.exe", $"stop {serviceName}")
            {
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false
            });
            stopProc?.WaitForExit(8000);
            using var delProc = Process.Start(new ProcessStartInfo("sc.exe", $"delete {serviceName}")
            {
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false
            });
            delProc?.WaitForExit(8000);
        }
        else
        {
            using var stop = Process.Start("systemctl", $"stop {serviceName}");
            stop?.WaitForExit(8000);
            using var disable = Process.Start("systemctl", $"disable {serviceName}");
            disable?.WaitForExit(8000);
        }
        Console.WriteLine("完成");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"忽略: {ex.Message}");
    }
    if (OperatingSystem.IsWindows())
    {
        Console.Write("  [2/4] 清理 PATH 环境变量... ");
        try
        {
            var machinePath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
            var paths = machinePath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var filtered = paths.Where(p =>
                !p.TrimEnd('\\').Equals(installDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ).ToArray();
            var newPath = string.Join(";", filtered);
            if (newPath != machinePath)
                Environment.SetEnvironmentVariable("Path", newPath, EnvironmentVariableTarget.Machine);
            Console.WriteLine("完成");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"跳过: {ex.Message}");
        }
    }
    else
    {
        Console.WriteLine("  [2/4] 跳过 PATH 清理（Linux 请手动移除 profile 配置）");
    }
    if (!cfg.KeepData)
    {
        Console.Write("  [3/4] 删除数据目录... ");
        try
        {
            if (Directory.Exists(dataDir))
                Directory.Delete(dataDir, true);
            Console.WriteLine("完成");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"跳过: {ex.Message}");
        }
    }
    else
    {
        Console.WriteLine("  [3/4] 跳过数据目录（已保留）");
    }
    Console.Write("  [4/4] 删除安装目录... ");
    try
    {
        if (!Directory.Exists(installDir))
        {
            Console.WriteLine("目录不存在，跳过");
        }
        else if (OperatingSystem.IsWindows())
        {
            foreach (var f in Directory.GetFiles(installDir))
            {
                if (f.Equals(exePath, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch { }
            }
            foreach (var d in Directory.GetDirectories(installDir))
            {
                try { Directory.Delete(d, true); } catch { }
            }
            var batPath = Path.Combine(Path.GetTempPath(), $"certguard_cleanup_{Guid.NewGuid():N}.bat");
            var batContent = $":loop\r\ndel /f /q \"{exePath}\" >nul 2>&1\r\nif exist \"{exePath}\" (\r\n    ping -n 3 127.0.0.1 >nul\r\n    goto loop\r\n)\r\nrmdir /s /q \"{installDir}\" >nul 2>&1\r\ndel /f /q \"{batPath}\" >nul 2>&1\r\n";
            File.WriteAllText(batPath, batContent);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c start /b \"\" \"{batPath}\"")
            {
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false
            });
            Console.WriteLine("完成（残留清理脚本已启动）");
        }
        else
        {
            Directory.Delete(installDir, true);
            Console.WriteLine("完成");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"跳过: {ex.Message}");
    }
    Console.WriteLine();
    Console.WriteLine("[√] CertGuard Agent 已卸载完成。");
    if (cfg.KeepData)
    {
        Console.WriteLine($"   数据目录已保留: {dataDir}");
        Console.WriteLine("   如需重新安装，请使用 --token 注册。");
    }
    return 0;
}

static bool IsLocalhost(Uri uri)
{
    var h = uri.Host.ToLowerInvariant();
    return h == "localhost" || h == "127.0.0.1" || h == "[::1]";
}

// ============================================================
// 辅助扩展
// ============================================================
file static class JsonEx
{
    public static bool TryGetPropertyValue(
        this System.Text.Json.JsonElement el, string name, out System.Text.Json.JsonElement val)
    {
        return el.TryGetProperty(name, out val) && val.ValueKind != System.Text.Json.JsonValueKind.Null;
    }
}





