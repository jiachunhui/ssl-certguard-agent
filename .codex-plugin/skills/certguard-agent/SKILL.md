---
name: certguard-agent
description: >
  Project conventions, pitfalls, and coding guidelines for the CertGuard Agent project (C#/.NET 8 SSL certificate deployment daemon).
  Use when: modifying error handling or logging patterns; implementing uninstall/cleanup/self-update logic;
  editing C# source with PowerShell or apply_patch; dealing with git restore rollbacks or file encoding issues;
  working with BackgroundService worker loops, IDeployProvider, or PlatformClient HMAC signing.
  Also applies to maintaining deploy/install scripts and the patch.py CI pipeline.
---

# CertGuard Agent

C# 12 / .NET 8 轻量级 SSL 证书自动部署守护进程。

## 技术栈

- 宿主：`Microsoft.Extensions.Hosting` / `BackgroundService`
- DI：`Microsoft.Extensions.DependencyInjection`
- HTTP：`HttpClient` + `IHttpClientFactory` → 封装在 `PlatformClient`
- 日志：Serilog（按天滚动文件 + 控制台）
- Windows 服务：`Microsoft.Extensions.Hosting.WindowsServices`

## 项目结构

```
src/CertGuard.Agent/
├── Models/         — AgentConfig, AgentInfo, PlatformApi (Req/Res/Task)
├── Services/
│   ├── PlatformClient.cs  — HMAC-SHA256 签名 + HTTP 封装
│   └── Providers/         — IDeployProvider + Nginx/Apache/IisProvider
├── Worker/
│   └── AgentWorker.cs     — 主循环：注册 → 心跳 → 拉任务 → 部署/更新/卸载
└── Program.cs             — 入口：参数解析 → Serilog → DI → 启动
deploy/             — 跨平台安装脚本
docs/               — 中文文档
patch.py            — CI/CD 补丁脚本
```

## 开发命令

```
dotnet build -c Debug                                    # 调试编译
dotnet publish -c Release -r linux-x64 --self-contained  # Linux x64 发布
dotnet publish -c Release -r win-x64 --self-contained    # Windows x64 发布
输出：src/build/<rid>/certguard-agent(.exe)
```

## 编码约定

- 源文件以 `// ============================================================` 注释头标记用途
- 命名空间：`CertGuard.Agent.{Models, Services, Worker}`
- API 模型用 `[JsonPropertyName]` 指定 JSON 字段名（camelCase/snake_case）
- 配置模型用 AppSettings 风格，平台路径用 `OperatingSystem.IsWindows()/IsLinux()` 分支
- Serilog 在 `Program.cs` 统一配置，Worker/Services 通过 `ILogger<T>` 注入
- 异步方法统一 `Async` 后缀，`CancellationToken` 作最后一个参数
- HTTP 签名逻辑统一在 `PlatformClient.Signed()` 封装
- `IDeployProvider` 通过 `ProviderFactory` 按平台优先级自动选择
- `deploy/install.ps1` 和 `deploy/install.sh` 必须同步维护
- 更新版本号时修改 `.csproj` 的 `<Version>` 和 `<AssemblyVersion>`
- 敏感配置（`agent_secret` / `token`）不硬编码，通过 `agent.json` 或环境变量读取

## 重要规则

1. **私钥不离开服务器** — Agent 不上传密钥文件内容
2. 不要 `dotnet run` 直接运行，项目设计为 self-contained 发布后以系统服务运行
3. 不要删除或清空 `agent.json`，注册后 `agent_id` / `agent_secret` 写入该文件
4. 不在日志明文记录 `agent_secret` 或 `registerToken`
5. 不要在 Worker 中 `new HttpClient()`，通过 DI 的 `IHttpClientFactory` 或已注入的 `PlatformClient`
6. 避免大循环中同步调用异步方法，心跳循环每个分支都必须传递 `CancellationToken`
7. 对 `PerformSelfUpdateAsync` / 卸载逻辑保持缄默，不产生意外副作用
8. 不要移除 `Program.cs` 中的 `IsLocalhost()` 辅助方法

---

## 踩坑记录

### 1. 两种"卸载"场景不可混淆

| 场景 | 触发 | 行为 |
|---|---|---|
| `AGENT_NOT_FOUND` | HTTP 401 + body 含"Agent 不存在" | 仅日志 → `StopApplication()`，不动本地文件 |
| `uninstall_agent` 任务 | 平台下发的任务 | 完整清理：停服务 → 清 PATH → 删数据目录 → 删安装目录 |

`ExecuteAsync` 中 `AGENT_NOT_FOUND` 由 `catch (InvalidOperationException ex) when (ex.Message.Contains("AGENT_NOT_FOUND"))` 捕获。
**不要误删这个 catch**——git restore 曾导致它丢失，401 落到兜底 `catch (Exception ex)` 输出完整堆栈。

### 2. 日志：可恢复 vs 终止

- **可恢复/重试**（注册重试、心跳、任务执行失败）：只记 `ex.Message`
  ```csharp
  _log.LogWarning("失败: {Error}", ex.Message);   // ✅
  _log.LogWarning(ex, "失败");                     // ❌ 输出完整堆栈
  ```
- **终止场景**（`LogCritical`）：保留完整异常用于调试
  ```csharp
  _log.LogCritical(ex, "严重错误");  // 保留异常
  ```

### 3. 文件编码

C# 源文件是 **UTF-8 无 BOM**。以下操作会改变编码：

| 操作 | 结果 | 应对 |
|---|---|---|
| `git show HEAD:file > tmp` | UTF-16 LE | 后续用 `UTF8Encoding::new($false)` 重写 |
| `Copy-Item` | 可能变 UTF-16 LE | 改用 `ReadAllText` + `WriteAllText` |
| `Set-Content -Encoding UTF8` | 带 BOM | 同上 |
| `WriteAllLines` 默认 | UTF-8 无 BOM | ✅ 安全 |

修复：`[System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($false))`

### 4. apply_patch 格式

- 所有行必须有前缀：`␣`（上下文匹配）、`+`（添加）、`-`（删除）
- `@@` **不是** unified diff 的 before/after 分隔符，只是 hunk 内的标记
- 空行匹配：patch 中写 `␣`（一个空格）
- 大段插入拆成多个小 patch
- 缩进必须与文件完全一致（1 个空格偏差就匹配失败）

### 5. PowerShell 编辑 C# 的避坑

- `@"..."@` here-string 结束符 `"@` 必须独占一行且无前导空格
- `$i:` 会被解析为 PSDrive（变量引用无效），应避免
- 含大量 `"` `\` `$` 的 C# 代码嵌入 PowerShell 极易出错，优先用 apply_patch
- 写 .ps1 脚本文件执行比 inline 命令可靠

### 6. git restore 的副作用

- `git restore` / `git checkout --` 丢失所有未提交修改
- 曾导致 `AGENT_NOT_FOUND` catch 块丢失、日志行回退
- 需保留修改时先备份文件再 restore

### 7. 服务自停的时序

`sc stop` 后 .NET 宿主关闭时等待 `ExecuteAsync` 结束（默认 ~30s）。
同步操作（`File.Delete`、`Directory.Delete`、`Process.Start`+`WaitForExit`）不检查 CancellationToken，在窗口期内可正常执行。

删除自身 exe 必须通过独立后台进程（`.bat` / `.sh`），不能放在主线程。