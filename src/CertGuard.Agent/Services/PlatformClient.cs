// ============================================================
// Services/PlatformClient.cs -- HMAC 签名 HTTP 客户端（核心通信层）
// ============================================================

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CertGuard.Agent.Models;
using Microsoft.Extensions.Logging;

namespace CertGuard.Agent.Services;

/// <summary>
/// 平台 API 通信客户端
/// 负责 HMAC 签名、HTTP 请求、注册/心跳/任务拉取/结果上报
/// </summary>
public class PlatformClient
{
    private readonly HttpClient _http;
    private readonly ILogger<PlatformClient> _log;
    private readonly JsonSerializerOptions _json;

    private string _agentId;
    private string _secret;
    private bool _registered;

    public PlatformClient(HttpClient http, ILogger<PlatformClient> log)
    {
        _http = http;
        _log = log;
        _json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        _agentId = "";
        _secret = "";
        _registered = false;
    }

    /// <summary>Agent 是否已注册（有 agentId + secret）</summary>
    public bool Registered => _registered;

    // ============================================================
    // 注册（使用一次性令牌，不走 HMAC）
    // ============================================================

    public async Task<(string agentId, string secret, string serverName)> RegisterAsync(
        string token, string osType, string osVer, string version, CancellationToken ct)
    {
        _log.LogInformation("正在注册到平台...");

        var body = new RegisterReq
        {
            RegisterToken = token,
            OsType = osType,
            OsVersion = osVer,
            AgentVersion = version
        };

        var resp = await _http.PostAsJsonAsync("/api/agent/register", body, _json, ct);
        resp.EnsureSuccessStatusCode();

        var result = await UnwrapAsync<RegisterRes>(resp, ct)
                     ?? throw new InvalidOperationException("注册响应为空");

        _agentId = result.AgentId;
        _secret = result.Secret;
        _registered = true;

        if (string.IsNullOrEmpty(_agentId))
            throw new InvalidOperationException("Server returned empty AgentId -- check platform registration logic");

        _log.LogInformation("注册成功。AgentId={Id}，服务器={Name}", result.AgentId, result.ServerName);
        return (result.AgentId, result.Secret, result.ServerName);
    }

    /// <summary>用已有的 agentId + secret 初始化（跳过注册）</summary>
    public void Init(string agentId, string secret)
    { _agentId = agentId; _secret = secret; _registered = true; }

    // ============================================================
    // 心跳
    // ============================================================

    public async Task<string?> PingAsync(string agentVersion, CancellationToken ct)
    {
        var body = new PingReq { Version = agentVersion };
        _log.LogInformation("发送心跳（版本={Version}）", agentVersion);
        var raw = await Signed(HttpMethod.Post, "/api/agent/ping", body, ct);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("succeeded", out var succeeded) || !succeeded.GetBoolean())
                return null;
            if (!root.TryGetProperty("data", out var data))
                return null;

            return data.TryGetProperty("latest_version", out var lv) ? lv.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    // ============================================================
    // 拉取任务
    // ============================================================

    public async Task<List<TaskItem>> FetchTasksAsync(CancellationToken ct)
    {
        var list = await UnwrapAsync<TaskListRes>(HttpMethod.Get, "/api/agent/tasks/pending", null, ct);
        return list?.Tasks ?? new();
    }

    // ============================================================
    // 上报任务结果
    // ============================================================

    public async Task ReportAsync(int taskId, bool success, string? detail, string? error, CancellationToken ct)
    {
        var body = new TaskResult
        {
            TaskId = taskId,
            Status = success ? "success" : "failed",
            Result = detail,
            Error = error
        };
        await Signed(HttpMethod.Post, "/api/agent/tasks/result", body, ct);
        _log.LogInformation("任务 {Id}: {Status}", taskId, body.Status);
    }

    // ============================================================
    // 上报环境
    // ============================================================

    public async Task ReportEnvAsync(EnvReport env, CancellationToken ct)
    {
        await Signed(HttpMethod.Post, "/api/agent/env/report", env, ct);
        _log.LogInformation("环境已上报: {Os} / {Web}", env.OsType, env.WebServer);
    }

    // ============================================================
    // 内部：HMAC 签名请求
    // ============================================================

    private async Task<string> Signed(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (!_registered)
            throw new InvalidOperationException("Agent 未注册");

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLower();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        // 构造签名字符串
        var msg = $"{method}|{path}|{nonce}|{ts}";
        var sig = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(_secret), Encoding.UTF8.GetBytes(msg))
        ).ToLower();

        var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation("X-Agent-Id", _agentId);
        req.Headers.TryAddWithoutValidation("X-Agent-Timestamp", ts);
        req.Headers.TryAddWithoutValidation("X-Agent-Nonce", nonce);
        req.Headers.TryAddWithoutValidation("X-Agent-Signature", sig);

        if (body is not null)
            req.Content = JsonContent.Create(body, options: _json);

        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // ============================================================
    // 内部：Furion 响应包装解析 -- {"succeeded": true, "data": {...}}
    // ============================================================

    private async Task<T?> UnwrapAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var raw = await Signed(method, path, body, ct);
        if (string.IsNullOrWhiteSpace(raw)) return default;
        return UnwrapCore<T>(raw, path);
    }

    private async Task<T?> UnwrapAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(raw)) return default;
        return UnwrapCore<T>(raw, "/api/agent/register");
    }

    private T? UnwrapCore<T>(string raw, string path)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("succeeded", out var succeeded) || !succeeded.GetBoolean())
        {
            _log.LogWarning("API 返回失败响应 {Path}: {Raw}", path, raw);
            return default;
        }

        if (!root.TryGetProperty("data", out var data))
        {
            _log.LogWarning("API 响应缺少 data 字段 {Path}: {Raw}", path, raw);
            return default;
        }

        if (data.ValueKind == JsonValueKind.Null)
            return default;

        return JsonSerializer.Deserialize<T>(data.GetRawText(), _json);
    }
}
