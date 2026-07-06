// ============================================================
// Program.cs — Agent 入口
// 用法:
//   首次: certguard-agent --token ct_reg_xxxxxx
//   更新密钥: certguard-agent --update-secret <新密钥>
//   更新版本: certguard-agent --update <下载地址>
//   日常: certguard-agent
// ============================================================

using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        case "--help": case "-h": showHelp = true; break;
        case "--version": case "-v": var vAsm = System.Reflection.Assembly.GetExecutingAssembly(); var vAttr = (System.Reflection.AssemblyInformationalVersionAttribute?)System.Attribute.GetCustomAttribute(vAsm, typeof(System.Reflection.AssemblyInformationalVersionAttribute)); var vs = vAttr?.InformationalVersion ?? vAsm.GetName().Version?.ToString() ?? "1.0.0"; Console.WriteLine($"certguard-agent v{vs}"); return 0;
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
  --version, -v    Show version
  --help, -h       Show this help

First run:
  certguard-agent --token ct_reg_xxxxxx

Update secret:
  certguard-agent --update-secret <new_secret>

Update version:
  certguard-agent --update <download_url>

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
        Console.WriteLine($"✅ 密钥已更新！配置文件: {configFile}");
        Console.WriteLine("   请重启服务使新密钥生效。");
        Console.WriteLine($"   Windows: Restart-Service CertGuardAgent");
        Console.WriteLine($"   Linux:   systemctl restart certguard-agent");
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

        Console.WriteLine($"✅ 更新完成！版本已升级到 {dir}");
        Console.WriteLine("   旧版本备份在: " + backupDir);
        Console.WriteLine("   请重启服务使新版本生效。");
        Console.WriteLine($"   Windows: Restart-Service CertGuardAgent");
        Console.WriteLine($"   Linux:   systemctl restart certguard-agent");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"错误: 更新失败 - {ex.Message}");
        return 1;
    }
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
    o.ServiceName = "CertGuardAgent";
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





