 # 常见问题

 > 高频问题与解决方案汇总

 ## 安装相关

 ### Q: Agent 支持哪些操作系统？

 支持 Linux（x86_64 / arm64）和 Windows（x86_64）。macOS 暂不支持。

 ### Q: 需要预装 .NET 运行时吗？

 不需要。Agent 使用 .NET 8 Self-contained 发布，已包含所有运行时依赖。

 ### Q: 安装时提示"权限不足"？

 Agent 安装和运行需要管理员 / root 权限，因为需要创建系统服务和读写 Web 服务配置。

 - Linux：使用 `sudo` 或以 root 用户执行
 - Windows：以**管理员身份**打开 PowerShell

 ### Q: 一键安装脚本执行失败？

 可能原因和解决方法：

 1. **网络不通**：检查服务器能否访问 `agent.topssl.cn`
 2. **缺少 curl**：Linux 上安装 curl：`apt install curl` 或 `yum install curl`
 3. **PowerShell 执行策略**（Windows）：以管理员身份运行 `Set-ExecutionPolicy RemoteSigned -Scope CurrentUser`
 4. **SSL/TLS 错误**（Windows）：执行 `[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12`

 > ![截图] 此处占位：一键安装成功的终端截图

 ## 注册相关

 ### Q: 如何获取注册令牌？

 登录 [TOPSSL.CN](https://topssl.cn) 控制台 → **Agent 管理** → **注册 Agent**，点击生成令牌。

 ### Q: 注册令牌过期了怎么办？

 令牌是一次性的，使用后即失效。如果过期或已使用，在控制台重新生成一个即可。

 ### Q: 注册失败，提示"无效令牌"？

- 确认令牌没有拼写错误
- 确认令牌未被使用过
- 在控制台重新生成令牌后重试

 ### Q: 注册成功后如何找到 Agent ID？

 查看 `agent.json` 配置文件中的 `agent_id` 字段，或查看 Agent 启动日志。

 ## 运行相关

 ### Q: Agent 服务正常运行，但证书没有自动部署？

 检查以下可能：

 1. 在 TOPSSL.CN 控制台确认已成功申请证书
 2. 查看 Agent 日志：`journalctl -u topssl-certguard-agent -n 50`
 3. 确认 Agent 处于在线状态（控制台 Agent 管理中查看）
 4. 确认心跳间隔内已尝试拉取任务

 ### Q: 如何修改心跳间隔？

 编辑 `agent.json` 中的 `heartbeat_sec` 字段，或启动时使用 `--heartbeat` 参数指定。

 ### Q: Agent 日志提示"平台无法连接"？

 ```log
 [WRN] 平台无法连接，下次心跳自动重试
 ```

 这是正常行为，Agent 会自动重试。如果持续存在：

 1. 检查 `agent.json` 中 `api_base_url` 是否正确
 2. 确认服务器网络可到达平台地址
 3. 检查防火墙是否阻止了出站连接

 ### Q: Agent 占用多少资源？

 Agent 非常轻量，空闲时内存占用约 20 MB，CPU 占用几乎为 0。

 ## 证书部署相关

 ### Q: 证书部署失败，Nginx 配置检查不通过？

 Agent 执行 `nginx -t` 验证配置。失败通常是因为：

 - Nginx 配置中引用了不存在的证书路径
 - Nginx 配置文件存在语法错误

 解决：手动执行 `nginx -t` 查看具体错误信息。

 ### Q: IIS 部署后证书未生效？

 1. 确认绑定的域名是否正确
 2. 执行 `iisreset` 手动重启 IIS
 3. 检查证书是否在 Windows 证书存储中（`certlm.msc` → 个人）
 4. 确认 HTTPS 绑定已正确添加

 ### Q: 如何确认证书已成功部署？

 使用 OpenSSL 验证：

 ```bash
 openssl s_client -connect your-domain.com:443 -servername your-domain.com </dev/null 2>/dev/null | openssl x509 -noout -subject -dates
 ```

 ## 更新相关

 ### Q: Agent 会自动更新吗？

 会。Agent 在每次心跳时检测平台端的最新版本，如果版本不一致则自动下载更新包并重启。

 ### Q: 手动更新时提示下载失败？

- 确认更新 URL 可访问
- 确认磁盘空间充足
- 确认 `/tmp` 目录可写

 ### Q: 更新后需要重启服务吗？

 自动更新完成后会自动重启服务。如果使用 `--update` 手动更新，Agent 会退出并启动更新脚本，脚本执行完后自动重启服务。

 ## 卸载相关

 ### Q: 卸载后如何彻底清理？

 执行 `certguard-agent --uninstall` 会自动清理。如需保留数据使用 `--keep-data`。

 Linux 下还需手动删除 systemd 服务文件：

 ```bash
 rm -f /etc/systemd/system/topssl-certguard-agent.service
 systemctl daemon-reload
 ```

 ### Q: 卸载后重新安装需要新令牌吗？

 如果卸载时保留了数据目录（`--keep-data`），可以直接启动 Agent 复用原有配置。否则需要重新获取注册令牌。

 ## 安全相关

 ### Q: Agent 会收集我的数据吗？

 Agent 只会上报必要的信息：操作系统类型和版本、Web 服务类型、服务器 IP 地址。不会收集任何用户数据、网站内容或访问日志。

 ### Q: 我的私钥安全吗？

 **私钥始终在您的服务器上**。Agent 只接收签名证书和任务指令，私钥的生成和存储完全在本地完成。平台不知道也不存储您的私钥。

 ### Q: Agent 的通信安全吗？

 所有 API 请求都经过 HMAC-SHA256 签名，并包含 nonce 和时间戳防止重放攻击。即使通信被截获，也无法伪造请求。

 ### Q: 如果 TOPSSL.CN 平台停运了，Agent 还能用吗？

 Agent 代码完全开源，您可以 Fork 后适配其他平台或自建后端。Agent 与平台的通信协议是清晰的 API 接口，兼容实现并不复杂。

 > 仍有疑问？请访问 **[服务支持](../07-服务支持/README.md)** 获取帮助。
