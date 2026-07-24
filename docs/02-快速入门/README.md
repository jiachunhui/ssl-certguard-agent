 # 快速入门

 > 30 秒让您的服务器接入 CertGuard 证书自动部署

 ## 前置条件

- 一台运行 Linux 或 Windows 的服务器
- 服务器上已安装 Nginx / Apache / IIS 之一
- 拥有 [TOPSSL.CN](https://topssl.cn) 账号

 ## 第一步：获取注册令牌

 登录 TOPSSL.CN 控制台，进入 **Agent 管理** → **注册 Agent**，生成一个注册令牌。

 令牌格式：`ct_reg_xxxxxxxxxxxxxxxx`

 > ![截图] 此处占位：TOPSSL.CN 控制台 Agent 管理页面截图，展示"生成注册令牌"按钮位置

 ## 第二步：一键安装

 ### Linux 服务器

 在服务器上执行以下命令（需要 root 权限）：

 ```bash
 curl -fsSL https://agent.topssl.cn/install | bash -s -- --token ct_reg_xxxxxx
 ```

 将命令中的 `ct_reg_xxxxxx` 替换为您的实际注册令牌。

 > ![截图] 此处占位：Linux 终端执行安装命令的截图，展示安装成功输出

 ### Windows 服务器

 以管理员身份打开 PowerShell，执行：

 ```powershell
 [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
 iex ((New-Object System.Net.WebClient).DownloadString('https://agent.topssl.cn/install.ps1'))
 Install-CertGuardAgent -Token ct_reg_xxxxxx
 ```

 > ![截图] 此处占位：Windows PowerShell 管理员执行安装命令的截图

 ## 第三步：验证运行状态

 安装完成后，Agent 会自动注册并启动为系统服务。

 ### Linux (systemd)

 ```bash
 systemctl status topssl-certguard-agent
 ```

 预期输出包含 `active (running)`。

 > ![截图] 此处占位：systemctl status 输出截图，展示服务运行中状态

 ### Windows (服务管理器)

 ```powershell
 Get-Service TopSSLCertGuardAgent
 ```

 预期输出 `Status` 为 `Running`。

 > ![截图] 此处占位：Windows 服务管理器或 Get-Service 输出截图

 ## 完成！

 Agent 已成功安装并运行。现在您可以在 TOPSSL.CN 控制台中为您的域名申请证书，Agent 会自动接收任务并部署到您的 Web 服务器上。

 > 接下来，建议阅读 **[操作指南](../04-操作指南/README.md)** 了解更多日常管理命令，或查看 **[实践教程](../05-实践教程/README.md)** 获取特定 Web 服务的详细配置。
