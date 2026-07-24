 # 开发指南

 > 面向开发者的构建、调试和贡献指南

 CertGuard Agent 使用 .NET 8 开发，基于 C#，欢迎开发者参与贡献。

 ## 章节导航

| 文档 | 说明 |
|------|------|
| [构建发布](./构建发布.md) | 从源码构建 Agent |
| [贡献指南](./贡献指南.md) | 如何参与开源贡献 |

 ## 技术栈

| 技术 | 说明 |
|------|------|
| 语言 | C# 12 |
| 框架 | .NET 8 |
| 服务框架 | Microsoft.Extensions.Hosting |
| HTTP 客户端 | IHttpClientFactory 注入 |
| 日志 | Serilog（控制台 + 文件滚动） |
| 打包 | Self-contained 单文件发布 |

 ## 项目结构

 ```
 ├── src/
 │   └── CertGuard.Agent/        # 主项目
 │       ├── Program.cs           # 入口：参数解析 + 服务注册
 │       ├── appsettings.json     # 运行时配置
 │       ├── Models/              # 数据模型
 │       │   ├── AgentConfig.cs   # Agent 配置类
 │       │   ├── AgentInfo.cs     # 版本信息
 │       │   └── PlatformApi.cs   # API 请求/响应模型
 │       ├── Services/            # 核心服务
 │       │   ├── PlatformClient.cs # HMAC 签名 HTTP 客户端
 │       │   └── Providers.cs     # 部署提供程序（Nginx/Apache/IIS）
 │       └── Worker/              # 后台任务
 │           └── AgentWorker.cs   # Agent 主循环
 ├── deploy/                      # 部署脚本
 │   ├── install.sh               # Linux 一键安装
 │   ├── install.ps1              # Windows 一键安装
 │   └── build/                   # 构建产物
 ├── LICENSE                      # Apache 2.0
 └── README.md                    # 项目首页
 ```

 > 开始贡献之前，请阅读 **[贡献指南](./贡献指南.md)** 了解编码规范和提交流程。
