content = open(r"D:\trae\demo3\CertGuard.Agent\src\CertGuard.Agent\Worker\AgentWorker.cs", "r", encoding="utf-8").read()

old1 = "    private readonly string _osVer;\n"
new1 = "    private readonly string _osVer;\n    private bool _envReported;\n"
content = content.replace(old1, new1, 1)

content = content.replace(
    "            IpAddress = await TryGetPublicIpAsync(ct)",
    "            IpAddress = GetLocalIpAddress()"
)

content = content.replace(
    "        }, ct);\n    }\n    catch (HttpRequestException ex)\n    {\n        _log.LogWarning(ex, \"环境上报失败，下次心跳自动重试\");",
    "        }, ct);\n        _envReported = true;\n        _log.LogInformation(\"环境上报成功: IP={}\", GetLocalIpAddress() ?? \"未获取到\");\n    }\n    catch (HttpRequestException ex)\n    {\n        _log.LogWarning(ex, \"环境上报失败（心跳周期重试）\");"
)

content = content.replace(
    "        // 心跳 — 获取最新版本号\n        var latestVer = await _client.PingAsync(_version, ct);\n\n        // 检查版本：不一致则自动更新",
    "        // 心跳 — 获取最新版本号\n        var latestVer = await _client.PingAsync(_version, ct);\n\n        // 环境未上报则在心跳中重试\n        if (!_envReported)\n        {\n            await SafeReportEnv(ct);\n        }\n\n        // 检查版本：不一致则自动更新"
)

old_method = "private static async Task<string?> TryGetPublicIpAsync\n{\n    try\n    {\n        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };\n        var ip = await http.GetStringAsync(\"https://api.ipify.org\", ct);\n        return ip.Trim();\n    }\n    catch\n    {\n        return null;\n    }\n}"
