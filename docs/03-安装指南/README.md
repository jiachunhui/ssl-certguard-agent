 # 安装指南

 > 详细的安装说明，覆盖 Linux 和 Windows 平台

 本文档提供 CertGuard Agent 的完整安装步骤。如果追求最快上手，请直接使用 [一键安装](../02-快速入门/README.md) 方式。

 ## 系统要求

| 项目 | 要求 |
|------|------|
| 操作系统 | Linux (x86_64 / arm64) 或 Windows (x86_64) |
| .NET 运行时 | 无需预装（使用 Self-contained 发布包） |
| 磁盘空间 | 最低 50 MB |
| 内存 | 最低 64 MB（空闲时约 20 MB） |
| 权限 | Linux root / Windows 管理员 |
| 网络 | 可访问 TOPSSL.CN API（默认 `http://localhost:5003`，生产环境需修改） |

 ## 安装方式选择

| 方式 | 适用场景 | 难度 |
|------|----------|------|
| [一键安装脚本](./02-快速入门/README.md) | 首次体验、快速部署 | ⭐ |
| [Linux 手动安装](./Linux手动安装.md) | 自定义安装路径、离线环境 | ⭐⭐ |
| [Windows 手动安装](./Windows手动安装.md) | 自定义安装、需要精细控制 | ⭐⭐ |

 ## 安装后检查清单

- [ ] Agent 服务正在运行 (`systemctl status` / `Get-Service`)
- [ ] 查看日志确认注册成功 (`journalctl -u topssl-certguard-agent` / `{DataDir}/logs/`)
- [ ] 在 TOPSSL.CN 控制台确认 Agent 状态为在线
- [ ] （可选）调整心跳间隔等配置

 > 遇到安装问题？请查阅 **[常见问题](../06-常见问题/README.md)** 或 **[服务支持](../07-服务支持/README.md)**。
