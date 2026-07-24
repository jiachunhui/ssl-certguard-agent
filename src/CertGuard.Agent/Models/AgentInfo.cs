// ============================================================
// Models/AgentInfo.cs — Agent 版本信息（统一版本号来源）
// ============================================================

using System.Reflection;

namespace CertGuard.Agent.Models;

/// <summary>
/// Agent 版本信息
/// 统一从 AssemblyInformationalVersionAttribute 获取并清理多余后缀
/// </summary>
internal static class AgentInfo
{
    /// <summary>干净的语义化版本号（已移除 +commit_hash 等后缀）</summary>
    public static string Version
    {
        get
        {
            var asm = typeof(AgentInfo).Assembly;
            var raw = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                       ?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "1.0.0";

            // 只取 + 号之前的语义化版本号（如 1.1.2+e6d7cca → 1.1.2）
            var idx = raw.IndexOf('+');
            return idx > 0 ? raw[..idx] : raw;
        }
    }
}
