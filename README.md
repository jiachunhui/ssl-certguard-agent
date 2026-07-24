# CertGuard Agent

> 开源证书自动部署守护进程 — 配合 [TOPSSL.CN](https://topssl.cn) 平台使用

## 简介

CertGuard Agent 是一个轻量级的后台守护进程，安装在你运行 Web 服务的服务器上，自动完成 SSL 证书的部署和续签。

**核心原则：私钥不出服务器**。所有密钥生成和证书部署操作完全在本地完成，平台只下发签名证书和任务指令。

## 支持的 Web 服务

| 服务 | 平台 | 状态 |
|------|------|------|
| Nginx | Linux | ✅ 已支持 |
| Apache | Linux | ✅ 已支持 |
| IIS | Windows | ✅ 已支持 |
| 通用文件模式 | 跨平台 | ✅ 已支持（兜底方案） |

## 快速开始

在 TOPSSL.CN 控制台 → Agent 管理 → 生成注册令牌，然后在服务器上执行:

```bash
# Linux
curl -fsSL https://agent.topssl.cn/install | bash -s -- --token ct_reg_xxxxxx

# Windows (PowerShell 管理员)
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
iex ((New-Object System.Net.WebClient).DownloadString('https://agent.topssl.cn/install.ps1'))
Install-CertGuardAgent -Token ct_reg_xxxxxx
```

## 手动安装

1. 下载对应平台的二进制文件
2. 执行注册:

```bash
# Linux
./certguard-agent --token ct_reg_xxxxxx

# Windows
certguard-agent.exe --token ct_reg_xxxxxx
```

3. 创建系统服务:

```bash
# Linux (systemd)
sudo cp deploy/certguard-agent.service /etc/systemd/system/
sudo systemctl enable --now certguard-agent

# Windows (sc.exe 管理员)
sc.exe create CertGuardAgent binPath="C:\Program Files\CertGuard\certguard-agent.exe"
sc.exe start CertGuardAgent
```

## 工作原理

```
┌─────────────┐     心跳 + 拉取任务     ┌──────────────┐
│  CertGuard   │ ◄────────────────────── │   Agent      │
│   Platform   │ ──────────────────────► │  (你的服务器) │
└─────────────┘     上报结果             └──────┬───────┘
                                               │
                                          ┌────▼────┐
                                          │  Nginx  │
                                          │  Apache │
                                          │   IIS   │
                                          └─────────┘
```

1. Agent 启动后自动检测本机安装的 Web 服务（Nginx → Apache → IIS → 文件模式）
2. 首次运行使用一次性令牌注册，之后使用 HMAC 签名通信
3. 每 60 秒发送心跳，同时拉取待执行任务
4. 收到 deploy_cert 任务后：写入证书 → 重载 Web 服务 → 上报结果

## 安全

- **私钥不出服务器**: 所有密钥生成和存储完全在本地
- **HMAC-SHA256 签名**: 每次 API 请求携带时间戳 + nonce + 签名
- **防重放攻击**: nonce + 5 分钟时间窗口验证
- **代码开源**: Apache 2.0 许可证，可审计

## 构建

```bash
# 需要 .NET 8 SDK
cd src/CertGuard.Agent

# Linux x64
dotnet publish -c Release -r linux-x64 --self-contained -o ../build/linux-x64

# Linux arm64
dotnet publish -c Release -r linux-arm64 --self-contained -o ../build/linux-arm64

# Windows x64
dotnet publish -c Release -r win-x64 --self-contained -o ../build/win-x64
```

## 许可证

Apache License 2.0 — 允许商业使用、修改和分发。
 
 ## 文档体系
 
 本项目提供完整的中文文档，位于 [docs/](./docs/README.md) 目录：
 
 | 章节 | 说明 |
 |------|------|
 | [概述](./docs/01-概述/README.md) | 项目介绍、为什么开源、安全架构 |
 | [快速入门](./docs/02-快速入门/README.md) | 30 秒上手体验 |
 | [安装指南](./docs/03-安装指南/README.md) | 各平台详细安装步骤 |
 | [操作指南](./docs/04-操作指南/README.md) | 命令行、配置、日志、维护 |
 | [实践教程](./docs/05-实践教程/README.md) | Nginx/Apache/IIS 真实场景 |
 | [常见问题](./docs/06-常见问题/README.md) | 高频问题与解决方案 |
 | [服务支持](./docs/07-服务支持/README.md) | 获取帮助与反馈 |
 | [开发指南](./docs/08-开发指南/README.md) | 构建、贡献、参与开源 |
