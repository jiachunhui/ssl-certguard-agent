// ============================================================
// Worker/AgentWorker.cs — Agent 主循环
// ============================================================

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using CertGuard.Agent.Models;
using CertGuard.Agent.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CertGuard.Agent.Worker;

public class AgentWorker : BackgroundService
{
    private readonly PlatformClient _client;
    private readonly IDeployProvider _deploy;
    private readonly AgentConfig _cfg;
    private readonly ILogger<AgentWorker> _log;
    private readonly IHostApplicationLifetime _life;
    private readonly string _version;
    private readonly string _osType;
    private readonly string _osVer;
    private bool _envReported;

    public AgentWorker(PlatformClient client, ProviderFactory factory,
        IOptions<AgentConfig> cfg, ILogger<AgentWorker> log, IHostApplicationLifetime life)
    {
        _client = client;
        _cfg = cfg.Value;
        _log = log;
        _life = life;
        _version = CertGuard.Agent.Models.AgentInfo.Version;
        var (provider, osType, osVer) = factory.Create();
        _deploy = provider;
        _osType = osType;
        _osVer = osVer;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            _log.LogInformation("CertGuard Agent v{Ver} 启动中...", _version);
            await EnsureIdentity(ct);
            if (_cfg.RegisterOnly)
            {
                _log.LogInformation("仅注册模式，退出");
                _life.StopApplication();
                return;
            }
            await SafeReportEnv(ct);
            _log.LogInformation("就绪。Web={Web}, 系统={Os}, 心跳={Hb}s", _deploy.Name, _osType, _cfg.HeartbeatSec);
            while (!ct.IsCancellationRequested)
            {
                await Cycle(ct);
                await Task.Delay(_cfg.HeartbeatSec * 1000, ct);
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No identity"))
        {
            _log.LogCritical(ex, "Agent 无身份信息且没有注册令牌");
            _life.StopApplication();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("AGENT_NOT_FOUND"))
        {
            _log.LogCritical("Agent 在平台上不存在，已停止运行");
            _life.StopApplication();
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Agent 已停止");
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "严重错误");
            _life.StopApplication();
        }
    }

private async Task SafeReportEnv(CancellationToken ct)
{
    try
    {
        await _client.ReportEnvAsync(new EnvReport
        {
            OsType = _osType,
            OsVersion = _osVer,
            WebServer = _deploy.Name,
            IpAddress = GetLocalIpAddress()
        }, ct);
        _envReported = true;
        _log.LogInformation("环境上报成功: IP={0}", GetLocalIpAddress() ?? "未获取到");
    }
    catch (HttpRequestException)
    {
        _log.LogWarning("环境上报失败（心跳周期重试）");
    }
}
private async Task Cycle(CancellationToken ct)
{
    try
    {
        // 心跳 — 获取最新版本号
        var latestVer = await _client.PingAsync(_version, ct);

        // 环境未上报则在心跳中重试
        if (!_envReported)
        {
            await SafeReportEnv(ct);
        }

        // 检查版本：不一致则自动更新
        if (!string.IsNullOrEmpty(latestVer) &&
            !string.Equals(latestVer, _version, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("版本不匹配：平台={LatestVer}，本地={LocalVer}", latestVer, _version);

            // 确定当前系统架构
            string arch;
            if (OperatingSystem.IsWindows())
                arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                    == System.Runtime.InteropServices.Architecture.Arm64
                    ? "win-arm64" : "win-x64";
            else if (OperatingSystem.IsLinux())
                arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                    == System.Runtime.InteropServices.Architecture.Arm64
                    ? "linux-arm64" : "linux-x64";
            else
            {
                _log.LogWarning("不支持的操作系统，无法自动更新");
                return;
            }

            var ext = OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";
            var downloadUrl = _cfg.ApiBaseUrl.TrimEnd('/') + "/agent/certguard-agent-" + arch + ext;
            await PerformSelfUpdateAsync(downloadUrl, ct);
            return;
        }

        // 拉任务
        var tasks = await _client.FetchTasksAsync(ct);
        if (tasks.Count == 0) return;

        _log.LogInformation("获取到 {N} 个任务", tasks.Count);

        foreach (var t in tasks)
            await DoTask(t, ct);
    }
    catch (HttpRequestException)
    {
        _log.LogWarning("平台无法连接，下次心跳自动重试");
    }
    catch (Exception ex)
    {
        _log.LogError("心跳周期异常: {Error}", ex.Message);
    }
}

private async Task DoTask(TaskItem t, CancellationToken ct)
{
    _log.LogInformation("任务 #{Id} 类型={Type}", t.TaskId, t.TaskType);

    try
    {
        switch (t.TaskType)
        {
            case "deploy_cert":
                await DoDeployCert(t, ct);
                break;

            case "update_agent":
                await DoUpdateAgent(t, ct);
                break;

           case "health_report":
               await _client.ReportAsync(t.TaskId, true,
                   JsonSerializer.Serialize(new { agent = "ok" }), null, ct);
               break;

            case "uninstall_agent":
                await DoUninstall(t, ct);
                break;

            default:
               await _client.ReportAsync(t.TaskId, false, null,
                   $"未知任务类型: {t.TaskType}", ct);
                break;
        }
    }
    catch (Exception ex)
    {
        _log.LogError("任务 #{Id} 执行失败: {Error}", t.TaskId, ex.Message);
        await _client.ReportAsync(t.TaskId, false, null, ex.Message, ct);
    }
}

private async Task DoUninstall(TaskItem t, CancellationToken ct)
{
    _log.LogInformation("任务 #{Id}: 开始卸载 Agent", t.TaskId);
    await _client.ReportAsync(t.TaskId, true, null, null, ct);
    await _client.UnregisterAsync(ct);
    var serviceName = OperatingSystem.IsWindows()
        ? "TopSSLCertGuardAgent"
        : "topssl-certguard-agent";
    var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
    var installDir = Path.GetDirectoryName(exePath) ?? ".";

    // ---- 1. 停服务 + 删服务 ----
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
    }
    catch (Exception ex)
    {
        _log.LogWarning("停服务时出现异常（忽略，继续清理）: {Error}", ex.Message);
    }

    // ---- 2. 清理 PATH（仅 Windows） ----
    if (OperatingSystem.IsWindows())
    {
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
        }
        catch (Exception ex)
        {
            _log.LogWarning("清理 PATH 时出现异常（忽略）: {Error}", ex.Message);
        }
    }

    // ---- 3. 删除数据目录 ----
    if (!_cfg.KeepData)
    {
        try
        {
            if (Directory.Exists(_cfg.DataDir))
                Directory.Delete(_cfg.DataDir, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning("删除数据目录时出现异常（忽略）: {Error}", ex.Message);
        }
    }

    // ---- 4. 删除安装目录 ----
    try
    {
        if (Directory.Exists(installDir))
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var f in Directory.GetFiles(installDir))
                {
                    if (f.Equals(exePath, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(f); } catch { }
                }
                foreach (var d in Directory.GetDirectories(installDir))
                    try { Directory.Delete(d, true); } catch { }
                var batPath = Path.Combine(Path.GetTempPath(), $"certguard_cleanup_{Guid.NewGuid():N}.bat");
                var c = $":loop\r\ndel /f /q \"{exePath}\" >nul 2>&1\r\nif exist \"{exePath}\" (\r\n    ping -n 3 127.0.0.1 >nul\r\n    goto loop\r\n)\r\nrmdir /s /q \"{installDir}\" >nul 2>&1\r\ndel /f /q \"{batPath}\" >nul 2>&1\r\n";
                File.WriteAllText(batPath, c);
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c start /b \"\" \"{batPath}\"")
                {
                    CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false
                });
            }
            else
            {
                Directory.Delete(installDir, true);
            }
        }
    }
    catch (Exception ex)
    {
        _log.LogWarning("删除安装目录时出现异常（忽略）: {Error}", ex.Message);
    }

    _log.LogInformation("任务 #{Id}: Agent 卸载完成，服务即将退出", t.TaskId);
    _life.StopApplication();
}

private async Task DoDeployCert(TaskItem t, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(t.Payload))
    {
        await _client.ReportAsync(t.TaskId, false, null, "任务数据为空", ct);
        return;
    }

    var payload = JsonSerializer.Deserialize<DeployPayload>(t.Payload);
    if (payload is null)
    {
        await _client.ReportAsync(t.TaskId, false, null, "任务数据格式错误", ct);
        return;
    }

    if (payload.Domains is null or { Length: 0 })
    {
        await _client.ReportAsync(t.TaskId, false, null, "任务未包含域名信息", ct);
        return;
    }

    var primaryDomain = payload.Domains[0];
    var certPem = payload.CertPem ?? "";
    var keyPem = payload.KeyPem ?? "";
    if (string.IsNullOrWhiteSpace(certPem) || string.IsNullOrWhiteSpace(keyPem))
    {
        await _client.ReportAsync(t.TaskId, false, null, "任务未包含证书内容", ct);
        return;
    }

    var ok = await _deploy.DeployAsync(certPem, keyPem, payload.Domains, ct);
    if (!ok)
    {
        var errorMsg = _deploy.LastError ?? "证书部署失败";
        await _client.ReportAsync(t.TaskId, false, null, errorMsg, ct);
        return;
    }

    ok = await _deploy.ReloadAsync(ct);
    if (!ok)
    {
        var errorMsg = _deploy.LastError ?? "Web 服务重载失败";
        await _client.ReportAsync(t.TaskId, false, null, errorMsg, ct);
        return;
    }

    await _client.ReportAsync(t.TaskId, true,
        JsonSerializer.Serialize(new { domain = primaryDomain, web = _deploy.Name }), null, ct);
}

private async Task DoUpdateAgent(TaskItem t, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(t.Payload))
    {
        await _client.ReportAsync(t.TaskId, false, null, "任务数据为空", ct);
        return;
    }

    var payload = JsonSerializer.Deserialize<UpdateAgentPayload>(t.Payload);
    if (payload == null || string.IsNullOrWhiteSpace(payload.DownloadUrl))
    {
        await _client.ReportAsync(t.TaskId, false, null, "更新任务数据无效", ct);
        return;
    }

    try
    {
        await _client.ReportAsync(t.TaskId, true,
            JsonSerializer.Serialize(new { message = "update started" }), null, ct);
        await PerformSelfUpdateAsync(payload.DownloadUrl, ct);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    catch (Exception ex)
    {
        _log.LogError("更新失败: {Error}", ex.Message);
        await _client.ReportAsync(t.TaskId, false, null, ex.Message, ct);
    }
}
private async Task PerformSelfUpdateAsync(string downloadUrl, CancellationToken ct)
{
    var exePath = Environment.GetCommandLineArgs()[0];
    var exeDir = Path.GetDirectoryName(exePath)!;
    var isWindows = OperatingSystem.IsWindows();
    var ext = isWindows ? ".zip" : ".tar.gz";
    var pkgPath = Path.Combine(Path.GetTempPath(), "certguard_update_" + Guid.NewGuid().ToString("N") + ext);
    var extractDir = Path.Combine(Path.GetTempPath(), "certguard_extract_" + Guid.NewGuid().ToString("N"));

    try
    {
        _log.LogInformation("正在下载更新包: {Url}", downloadUrl);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var bytes = await http.GetByteArrayAsync(downloadUrl, ct);
        await File.WriteAllBytesAsync(pkgPath, bytes, ct);

        _log.LogInformation("下载完成（{Size} KB），正在解压...", bytes.Length / 1024);

        Directory.CreateDirectory(extractDir);
        if (isWindows)
            ZipFile.ExtractToDirectory(pkgPath, extractDir);
        else
            ExtractTarGz(pkgPath, extractDir);

        var backupDir = exeDir + "_backup_" + DateTime.Now.ToString("yyyyMMddHHmmss");
        _log.LogInformation("正在备份当前版本到 {BackupDir}", backupDir);
        Directory.CreateDirectory(backupDir);

        foreach (var f in Directory.GetFiles(exeDir))
        {
            var name = Path.GetFileName(f);
            if (name.Equals("agent.json", StringComparison.OrdinalIgnoreCase))
                continue;
            if (f.Equals(exePath, StringComparison.OrdinalIgnoreCase))
                continue;
            try { File.Move(f, Path.Combine(backupDir, name)); } catch { }
        }

        if (OperatingSystem.IsWindows())
            await WriteWindowsUpdateScript(exeDir, extractDir, ct);
        else
            await WriteLinuxUpdateScript(exeDir, extractDir, ct);

        _log.LogInformation("更新就绪，正在启动更新脚本并停止 Agent...");
        StartUpdateScriptDetached(exeDir);
        _life.StopApplication();
    }
    finally
    {
        try { if (File.Exists(pkgPath)) File.Delete(pkgPath); } catch { }
    }
}
private async Task WriteWindowsUpdateScript(string exeDir, string extractDir, CancellationToken ct)
{
    var scriptPath = Path.Combine(exeDir, "update.bat");
    var bat = "@echo off\r\n"
            + "ping -n 6 127.0.0.1 > nul\r\n"
            + "echo [CertGuard] Copying new files...\r\n"
            + "xcopy /y /e /q \"" + extractDir + "\\*\" \"" + exeDir + "\\\" > nul 2>&1\r\n"
            + "echo [CertGuard] Starting service TopSSLCertGuardAgent...\r\n"
            + "sc start TopSSLCertGuardAgent > nul 2>&1 || sc start CertGuardAgent > nul 2>&1\r\n"
            + "echo [CertGuard] Update complete.\r\n"
            + "del \"%~f0\"\r\n";
    await File.WriteAllTextAsync(scriptPath, bat, ct);
    _log.LogInformation("更新脚本已写入: {Path}", scriptPath);
}

private async Task WriteLinuxUpdateScript(string exeDir, string extractDir, CancellationToken ct)
{
    var scriptPath = Path.Combine(exeDir, "update.sh");
    var sh = "#!/bin/bash\n"
            + "sleep 5\n"
            + "echo '[CertGuard] Copying new files...'\n"
            + "cp -rf " + extractDir + "/* " + exeDir + "/\n"
            + "chmod +x " + exeDir + "/certguard-agent\n"
            + "echo '[CertGuard] Starting service topssl-certguard-agent...'\n"
            + "systemctl restart topssl-certguard-agent 2>/dev/null || systemctl restart certguard-agent 2>/dev/null\n"
            + "echo '[CertGuard] Update complete.'\n"
            + "rm -- \"$0\"\n";
    await File.WriteAllTextAsync(scriptPath, sh, ct);
    if (OperatingSystem.IsLinux())
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    _log.LogInformation("更新脚本已写入: {Path}", scriptPath);
}

private void StartUpdateScriptDetached(string exeDir)
{
    if (OperatingSystem.IsWindows())
    {
        var scriptPath = Path.Combine(exeDir, "update.bat");
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c start \"\" \"" + scriptPath + "\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }
    else
    {
        var scriptPath = Path.Combine(exeDir, "update.sh");
        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = scriptPath,
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }
    _log.LogInformation("更新脚本已启动（独立进程）");
}

/// <summary>使用系统 tar 命令解压 .tar.gz 文件（仅 Linux）</summary>
private static void ExtractTarGz(string tarGzPath, string destDir)
{
    using var proc = Process.Start(new ProcessStartInfo
    {
        FileName = "tar",
        Arguments = $"-xzf \"{tarGzPath}\" -C \"{destDir}\"",
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardError = true
    });
    proc?.WaitForExit();
    if (proc?.ExitCode != 0)
    {
        var err = proc?.StandardError.ReadToEnd() ?? "";
        throw new InvalidOperationException($"tar 解压失败 (exit={proc?.ExitCode}): {err}");
    }
}

private async Task EnsureIdentity(CancellationToken ct)
{
    if (!string.IsNullOrEmpty(_cfg.AgentId) && !string.IsNullOrEmpty(_cfg.AgentSecret))
    {
        _client.Init(_cfg.AgentId, _cfg.AgentSecret);
        _log.LogInformation("已加载身份信息: {Id}", _cfg.AgentId);
        return;
    }

    if (string.IsNullOrWhiteSpace(_cfg.RegisterToken))
        throw new InvalidOperationException("没有身份也没有注册令牌。首先使用- token <token >运行。");

    var token = _cfg.RegisterToken;
    var retryDelay = TimeSpan.FromSeconds(10);

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var (id, secret, _) = await _client.RegisterAsync(
                token, _osType, _osVer, _version, ct);

            _cfg.AgentId = id;
            _cfg.AgentSecret = secret;
            _cfg.RegisterToken = null;
            PersistConfig();
            _log.LogInformation("注册成功，已保存。ID={Id}", id);
            return;
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("平台连接失败（注册）: {Error}，将在 {Delay} 秒后重试...", ex.Message,
                (int)retryDelay.TotalSeconds);
            await Task.Delay(retryDelay, ct);
            retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 120));
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning("注册失败（业务拒绝）: {Error}，将在 {Delay} 秒后重试...", ex.Message,
                (int)retryDelay.TotalSeconds);
            await Task.Delay(retryDelay, ct);
            retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 120));
        }
    }
}

private void PersistConfig()
{
    var dir = !string.IsNullOrEmpty(_cfg.ConfigWritePath)
        ? Path.GetDirectoryName(_cfg.ConfigWritePath)!
        : _cfg.DataDir;
    var path = !string.IsNullOrEmpty(_cfg.ConfigWritePath)
        ? _cfg.ConfigWritePath
        : Path.Combine(_cfg.DataDir, "agent.json");

    var json = JsonSerializer.Serialize(new
    {
        api_base_url = _cfg.ApiBaseUrl,
        agent_id = _cfg.AgentId,
        agent_secret = _cfg.AgentSecret,
        heartbeat_sec = _cfg.HeartbeatSec,
        data_dir = _cfg.DataDir
    }, new JsonSerializerOptions { WriteIndented = true });

    Directory.CreateDirectory(dir);
    File.WriteAllText(path, json);

    if (OperatingSystem.IsLinux())
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

    _log.LogInformation("配置已保存到: {Path}", path);
}

/// <summary>从本地网络接口获取 IPv4 地址（排除回环/隧道/虚拟网卡）</summary>
private static string? GetLocalIpAddress()
{
    try
    {
        return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                      && ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                      && ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
            .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
            .Where(ua => ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                      && !System.Net.IPAddress.IsLoopback(ua.Address))
            .Select(ua => ua.Address.ToString())
            .FirstOrDefault();
    }
    catch
    {
        return null;
    }
}
}
