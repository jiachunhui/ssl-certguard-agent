// ============================================================
// Models/AgentConfig.cs — Agent 运行时配置
// ============================================================

namespace CertGuard.Agent.Models;

/// <summary>
/// Agent 运行配置，从命令行参数 / 配置文件 / 环境变量加载
/// </summary>
public class AgentConfig
{
    /// <summary>平台 API 地址（如 http://localhost:5003）</summary>
    public string ApiBaseUrl { get; set; } = "http://localhost:5003";

    /// <summary>Agent 唯一标识（首次注册后由平台分配写入）</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>HMAC 签名密钥（首次注册后由平台分配写入）</summary>
    public string AgentSecret { get; set; } = string.Empty;

    /// <summary>心跳间隔（秒），默认 60</summary>
    public int HeartbeatSec { get; set; } = 60;

    /// <summary>数据目录：用于存储证书等运行时数据。
    /// Windows 默认 %ProgramData%\CertGuard，Linux 默认 /var/lib/certguard</summary>
    public string DataDir { get; set; } = OperatingSystem.IsWindows()
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CertGuard")
        : "/var/lib/certguard";

    /// <summary>一次性注册令牌（仅首次运行需要，注册成功后清空）</summary>
    public string? RegisterToken { get; set; }

    /// <summary>仅注册后退出（安装脚本使用）</summary>
    public bool RegisterOnly { get; set; }

    /// <summary>允许不安全 SSL 连接（跳过证书验证，自签证书环境使用）</summary>
    public bool AllowInsecure { get; set; }

    /// <summary>配置文件写入路径。
    /// 若 exe 同目录存在 agent.json 则写入该路径，否则写入 DataDir/agent.json</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ConfigWritePath { get; set; }

    /// <summary>更新密钥（由 --update-secret 传入，替换本地 agent.json 中的 agent_secret）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? NewSecret { get; set; }
}
