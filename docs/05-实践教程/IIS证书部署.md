 # IIS 证书部署教程

 > Windows + IIS 环境下的证书自动部署

 ## 场景说明

 您的 Windows 服务器运行 IIS 并托管了 `example.com` 网站，希望使用 CertGuard Agent 自动管理 SSL 证书。

 ## 前提条件

- Windows Server 2016 / 2019 / 2022 或 Windows 10 / 11
- 已安装 IIS（Internet Information Services）
- IIS 中已创建 `example.com` 的站点
- 已完成 CertGuard Agent 安装（参考 [快速入门](../02-快速入门/README.md)）

 ## 工作流程

 Agent 启动后会自动完成以下操作：

 ### 1. 自动检测 IIS

 Agent 检测到操作系统为 Windows，且 `appcmd.exe` 存在，自动选择 IisProvider。

 ### 2. 接收部署任务

 在 TOPSSL.CN 控制台为 `example.com` 申请证书后，Agent 接收到 `deploy_cert` 任务。

 ### 3. 证书转换

 Agent 将 PEM 格式的证书和私钥转换为 PFX 格式（PKCS#12），用于导入 Windows 证书存储。

 ### 4. 导入证书存储

 通过 `certutil` 将 PFX 导入到 Windows **本地计算机 > 个人** 证书存储中。

 ### 5. 绑定 HTTPS

 Agent 自动完成以下操作：

 - 扫描 IIS 中所有站点，查找与目标域名匹配的绑定
 - 为匹配的站点添加 HTTPS（443 端口）绑定
 - 通过 PowerShell 将导入的证书指纹分配给 HTTPS 绑定
 - 启用 SNI（服务器名称指示）

 ### 6. 重置 IIS

 执行 `iisreset` 使新绑定和证书生效。

 ## IIS 站点配置示例

 以下是 IIS 站点手动配置的参考（Agent 会自动完成绑定，但您需要先创建站点）：

 ```
 站点名称: example.com
 物理路径: C:\inetpub\wwwroot\example.com
 绑定:
   - HTTP 80:  example.com
 ```

 Agent 会自动添加 HTTPS 443 绑定并分配证书。

 ## 验证部署结果

 ### 查看 Agent 日志

 ```powershell
 Get-Content "$env:ProgramData\TopSSL-CertGuard-Agent\logs\certguard-agent-*.log" -Tail 20
 ```

 预期输出：

 ```
 [INF] 正在导入 IIS 证书...
 [INF] 解析到 2 个 IIS 站点
 [INF] 站点: [1] Default Web Site
 [INF] 站点: [2] example.com
 [INF] IIS 证书部署完成：example.com，哈希=XXXX...。更新 1 个绑定，跳过 0 个。
 [INF] IIS 重载完成
 ```

 > ![截图] 此处占位：PowerShell 中查看 Agent 日志的截图，展示 IIS 部署成功

 ### 验证 HTTPS 绑定

 ```powershell
 & "$env:SystemRoot\system32\inetsrv\appcmd.exe" list site
 ```

 确认 `example.com` 站点显示 HTTPS 绑定。

 > ![截图] 此处占位：appcmd list site 输出截图，展示站点包含 HTTPS 绑定

 ### 浏览器访问

 在浏览器中访问 `https://example.com`，确认证书有效且无安全警告。

 ## 排错指南

 | 现象 | 可能原因 | 解决 |
|------|----------|------|
| Agent 未检测到 IIS | appcmd.exe 不在标准路径 | 确认 IIS 已正确安装 |
| 证书导入失败 | PFX 格式问题 | 查看日志中 certutil 错误输出 |
| 绑定添加失败 | 站点不存在或端口冲突 | 确认站点已存在，检查 443 端口是否被占用 |
| SSL 证书未分配 | PowerShell 执行受限 | 以管理员身份运行，检查执行策略 |
| SNI 启用失败 | appcmd 语法差异 | 不影响功能，可手动在 IIS 管理器中启用 SNI |

 ### 检查 Windows 防火墙

 确保 443 端口已开放：

 ```powershell
 netsh advfirewall firewall show rule name="HTTPS" dir=in
 ```

 > 更多问题请查看 **[常见问题](../06-常见问题/README.md)**。
