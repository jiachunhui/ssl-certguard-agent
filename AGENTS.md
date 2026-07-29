# CertGuard Agent

轻量级 SSL 证书自动部署守护进程，配合 TOPSSL.CN 平台使用。Agent 在服务器上以后台服务/守护进程运行，自动完成 Nginx / Apache / IIS 的证书部署与续签。

## 技术栈
- 语言 / 运行时：C# 12 / .NET 8
- 宿主模型：Microsoft.Extensions.Hosting / BackgroundService
- 依赖注入：Microsoft.Extensions.DependencyInjection
- HTTP 通信：HttpClient + Microsoft.Extensions.Http
- 日志：Serilog（按天滚动文件 + 控制台）
- Windows 服务：Microsoft.Extensions.Hosting.WindowsServices

## 项目结构
- src/CertGuard.Agent/ — 主项目入口与全部源码
  - Models/ — 配置模型（AgentConfig）、版本信息（AgentInfo）、API 数据模型（PlatformApi 中的 Req/Res/Task 类型）
  - Services/ — 核心通信层（PlatformClient，HMAC-SHA256 签名 + HTTP 封装）和部署提供程序（Providers 中的 IDeployProvider 接口与 NginxProvider/ApacheProvider/IisProvider 实现）
  - Worker/ — 主循环 AgentWorker（注册 → 心跳 → 拉取任务 → 部署/更新）
  - Program.cs — 程序入口：参数解析 → 配置加载 → Serilog 初始化 → DI 容器搭建 → 启动
- deploy/ — 跨平台安装脚本（install.sh / install.ps1）和预编译二进制
- docs/ — 完整中文文档（概述/快速入门/安装/操作/实践/FAQ/支持/开发）
- patch.py — 自动补丁脚本（用于 CI/CD 中更新 AgentWorker 行为）

## 开发命令
- dotnet build -c Debug — 调试编译
- dotnet publish -c Release -r linux-x64 --self-contained — Linux x64 发布
- dotnet publish -c Release -r win-x64 --self-contained — Windows x64 发布
- dotnet publish -c Release -r linux-arm64 --self-contained — Linux ARM64 发布
- 输出目录：src/build/<rid>/certguard-agent（或 .exe）

## 编码约定
- 所有源文件以 // ============================================================ 注释头标记用途和归属
- 命名空间使用 CertGuard.Agent.{Models, Services, Worker}，根命名空间为 CertGuard.Agent
- 对 API 数据模型使用 System.Text.Json 的 [JsonPropertyName] 属性指定 JSON 字段名，属性保持 camelCase / snake_case 与后端对齐
- 配置模型使用 AppSettings 风格的属性默认值，平台不可知路径用 OperatingSystem.IsWindows() / IsLinux() 做条件判断
- Serilog 在 Program.cs 中统一配置，Worker 和 Services 通过 ILogger<T> 注入，禁止直接在业务逻辑中实例化 Logger
- 异步方法统一使用 Async 后缀，CancellationToken 作为最后一个参数向下传递
- HTTP 请求签名逻辑统一在 PlatformClient.Signed() 方法中封装，不暴露 HMAC 算法细节到调用方
- IDeployProvider 接口实现（NginxProvider / ApacheProvider / IisProvider）各自独立一个类文件，通过 ProviderFactory 按平台优先级自动选择
- 对 deploy/install.ps1 和 deploy/install.sh 的修改必须同时维护两份脚本行为一致
- 更新版本号时修改 .csproj 的 <Version> 和 <AssemblyVersion> 两个属性
- 所有敏感配置（agent_secret / token）不硬编码，始终通过 agent.json 或环境变量读取
- 跨平台文件操作使用 OperatingSystem.IsWindows() / IsLinux() 进行分支，Path.Combine() 构建路径

## 重要规则
- 永远不要让私钥离开服务器：Agent 不向任何外部服务上传密钥文件内容
- 不要通过 dotnet run 直接运行，项目设计为 self-contained 发布后以系统服务运行
- 不要删除或清空 agent.json，注册后 agent_id 和 agent_secret 写入该文件用于后续 HMAC 签名通信
- 不要使用明文敏感日志记录 agent_secret 或 registerToken 的值
- 不要在 Worker 中直接使用 new HttpClient()，通过 DI 的 IHttpClientFactory / 已注入的 PlatformClient 发起 HTTP 请求
- 避免在大循环中同步调用异步方法，心跳循环中的每个分支都必须传递 CancellationToken
- 对 PerformSelfUpdateAsync / UninstallAgent 等自更新卸载逻辑保持缄默，不产生意外副作用
- 不要移除 Program.cs 中 static bool IsLocalhost(Uri) 辅助方法：它用于内网自签证书场景的 AllowInsecure 自动启用
