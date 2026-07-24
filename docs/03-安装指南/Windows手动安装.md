 # Windows 手动安装

 > 适用于自定义安装路径或需要精细控制的场景

 ## 下载二进制文件

 从 GitHub Releases 或 TOPSSL.CN 下载 Windows 二进制包：

 ```powershell
 Invoke-WebRequest -Uri https://agent.topssl.cn/download/certguard-agent-win-x64.zip -OutFile certguard-agent-win-x64.zip
 ```

 解压到安装目录（例如 `C:\Program Files\CertGuard`）：

 ```powershell
 Expand-Archive -Path certguard-agent-win-x64.zip -DestinationPath "C:\Program Files\CertGuard"
 ```

 > ![截图] 此处占位：Windows 文件资源管理器展示解压后的安装目录内容

 ## 可选：添加 PATH 环境变量

 将安装目录添加到系统 PATH，方便直接调用命令：

 ```powershell
 [Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path", "Machine") + ";C:\Program Files\CertGuard", "Machine")
 ```

 ## 注册 Agent

 以管理员身份打开 PowerShell，执行注册：

 ```powershell
 & "C:\Program Files\CertGuard\certguard-agent.exe" --token ct_reg_xxxxxx --register-only
 ```

 执行后会在 `%ProgramData%\TopSSL-CertGuard-Agent\` 下生成 `agent.json` 配置文件。

 > ![截图] 此处占位：PowerShell 执行注册命令的截图

 ## 创建 Windows 服务

 ```powershell
 sc.exe create TopSSLCertGuardAgent binPath="C:\Program Files\CertGuard\certguard-agent.exe" start=auto
 sc.exe start TopSSLCertGuardAgent
 ```

 或使用 PowerShell：

 ```powershell
 New-Service -Name TopSSLCertGuardAgent -BinaryPathName "C:\Program Files\CertGuard\certguard-agent.exe" -StartupType Automatic
 Start-Service TopSSLCertGuardAgent
 ```

 ## 验证安装

 ```powershell
 Get-Service TopSSLCertGuardAgent
 ```

 确认 `Status` 为 `Running`。

 > ![截图] 此处占位：Get-Service 输出截图，展示服务运行状态

 ## 查看 Windows 事件日志

 Agent 通过 Windows 服务方式运行，日志可查看：

 ```powershell
 Get-Content "$env:ProgramData\TopSSL-CertGuard-Agent\logs\certguard-agent-*.log" -Tail 50
 ```

 > ![截图] 此处占位：日志文件内容截图，展示正常运行的日志输出

 ## 配置自定义 API 地址

 如果使用自建平台，需要指定 API 地址：

 ```powershell
 & "C:\Program Files\CertGuard\certguard-agent.exe" --server https://your-platform.com --token ct_reg_xxxxxx --register-only
 ```

 或编辑配置文件 `%ProgramData%\TopSSL-CertGuard-Agent\agent.json`，修改 `api_base_url` 字段。

 > 详情查看 **[配置文件说明](../04-操作指南/配置文件说明.md)** 了解更多配置项。
