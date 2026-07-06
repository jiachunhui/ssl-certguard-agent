// ============================================================
// Models/PlatformApi.cs — 与平台 API 交互的数据模型
// ============================================================

using System.Text.Json.Serialization;

namespace CertGuard.Agent.Models;

/// <summary>注册请求</summary>
public class RegisterReq
{
    [JsonPropertyName("registerToken")] public string RegisterToken { get; set; } = "";
    [JsonPropertyName("osType")] public string OsType { get; set; } = "";
    [JsonPropertyName("osVersion")] public string OsVersion { get; set; } = "";
    [JsonPropertyName("agentVersion")] public string AgentVersion { get; set; } = "";
}

/// <summary>注册响应</summary>
public class RegisterRes
{
    [JsonPropertyName("agent_id")] public string AgentId { get; set; } = "";
    [JsonPropertyName("secret")] public string Secret { get; set; } = "";
    [JsonPropertyName("server_name")] public string ServerName { get; set; } = "";
}

    /// <summary>心跳响应</summary>
    public class PingRes
    {
        [JsonPropertyName("next_poll_ms")] public int NextPollMs { get; set; } = 60000;
        [JsonPropertyName("agent_status")] public string AgentStatus { get; set; } = "online";

        /// <summary>平台端最新版本号，Agent 用于判断是否需要自动更新</summary>
        [JsonPropertyName("latest_version")]
        public string? LatestVersion { get; set; }
    }

/// <summary>心跳请求</summary>
public class PingReq
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}

/// <summary>平台下发的任务</summary>
    public class TaskItem
    {
    [JsonPropertyName("task_id")] public int TaskId { get; set; }
    [JsonPropertyName("task_type")] public string TaskType { get; set; } = "";
    [JsonPropertyName("cert_id")] public string? CertId { get; set; }
    [JsonPropertyName("payload")] public string? Payload { get; set; }
}

/// <summary>任务拉取响应</summary>
public class TaskListRes
{
    [JsonPropertyName("tasks")] public List<TaskItem> Tasks { get; set; } = new();
}

/// <summary>任务结果上报请求</summary>
public class TaskResult
{
    [JsonPropertyName("taskId")] public int TaskId { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "success";
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>环境上报请求</summary>
public class EnvReport
{
    [JsonPropertyName("osType")] public string OsType { get; set; } = "";
    [JsonPropertyName("osVersion")] public string OsVersion { get; set; } = "";
    [JsonPropertyName("webServer")] public string WebServer { get; set; } = "";
    [JsonPropertyName("ipAddress")] public string? IpAddress { get; set; }
    [JsonPropertyName("sites")] public string? Sites { get; set; }
}

/// <summary>deploy_cert 任务的 payload 结构</summary>
public class DeployPayload
{
    [JsonPropertyName("cert_id")] public string CertId { get; set; } = "";

    [JsonPropertyName("domains")] public string[]? Domains { get; set; }

    /// <summary>PEM 格式证书内容（完整链，由平台从 CA 获取后下发）</summary>
    [JsonPropertyName("cert_pem")] public string? CertPem { get; set; }

    /// <summary>PEM 格式私钥内容</summary>
    [JsonPropertyName("key_pem")] public string? KeyPem { get; set; }
}

/// <summary>update_agent 任务的 payload 结构</summary>
public class UpdateAgentPayload
{
    [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
}
