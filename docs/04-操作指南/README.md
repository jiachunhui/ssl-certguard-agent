 # 操作指南

 > 日常使用和管理 CertGuard Agent 的操作手册

 本文档涵盖 Agent 的日常运维操作，包括命令行使用、配置文件管理、日志查看等。

 ## 章节导航

| 文档 | 说明 |
|------|------|
| [命令行参考](./命令行参考.md) | 所有命令参数的完整说明 |
| [配置文件说明](./配置文件说明.md) | agent.json 配置详解 |
| [日志管理](./日志管理.md) | 日志查看、分析和轮转配置 |
| [更新与卸载](./更新与卸载.md) | 升级 Agent 版本和卸载操作 |

 ## 日常操作速查

| 操作 | 命令 |
|------|------|
| 查看服务状态 | Linux: `systemctl status topssl-certguard-agent` / Windows: `Get-Service TopSSLCertGuardAgent` |
| 查看实时日志 | Linux: `journalctl -u topssl-certguard-agent -f` / Windows: `Get-Content {DataDir}\logs\*.log -Tail 50 -Wait` |
| 更新密钥 | `certguard-agent --update-secret <新密钥>` |
| 更新版本 | `certguard-agent --update <下载地址>` |
| 卸载 Agent | `certguard-agent --uninstall` |
| 仅注册 | `certguard-agent --token <令牌> --register-only` |

 > 首次使用？建议先阅读 **[快速入门](../02-快速入门/README.md)**。
