 # Linux 手动安装

 > 适用于自定义安装路径、离线环境或无 root 权限的场景

 ## 下载二进制文件

 从 GitHub Releases 或 TOPSSL.CN 下载对应架构的二进制包：

 ```bash
# x86_64
wget https://agent.topssl.cn/download/certguard-agent-linux-x64.zip

# ARM64（如树莓派、AWS Graviton）
wget https://agent.topssl.cn/download/certguard-agent-linux-arm64.zip
```

 解压：

 ```bash
 unzip certguard-agent-linux-x64.zip -d /opt/certguard
 chmod +x /opt/certguard/certguard-agent
 ```

 ## 注册 Agent

 使用一次性注册令牌进行注册：

 ```bash
 /opt/certguard/certguard-agent --token ct_reg_xxxxxx --register-only
 ```

 成功后会在 `/var/lib/TopSSL-CertGuard-Agent/` 下生成 `agent.json` 配置文件。

 > ![截图] 此处占位：Linux 终端执行注册命令的截图，展示注册成功输出

 ## 创建 Systemd 服务

 创建服务文件 `/etc/systemd/system/topssl-certguard-agent.service`：

 ```ini
 [Unit]
 Description=TopSSL CertGuard Agent
 After=network.target

 [Service]
 Type=simple
 ExecStart=/opt/certguard/certguard-agent
 Restart=always
 RestartSec=10
 User=root

 [Install]
 WantedBy=multi-user.target
 ```

 启动服务：

 ```bash
 systemctl daemon-reload
 systemctl enable --now topssl-certguard-agent
 ```

 ## 验证安装

 ```bash
 systemctl status topssl-certguard-agent
# 查看实时日志
 journalctl -u topssl-certguard-agent -f
 ```

 > ![截图] 此处占位：systemctl status 输出截图，展示服务 active (running) 状态

 ## 配置自定义 API 地址

 如果使用自建平台，需要指定 API 地址：

 ```bash
# 通过命令行参数
 /opt/certguard/certguard-agent --server https://your-platform.com --token ct_reg_xxxxxx --register-only

# 或编辑配置文件
 vi /var/lib/TopSSL-CertGuard-Agent/agent.json
# 修改 "api_base_url" 字段
 ```

 ## 离线安装

 如果服务器无法访问外网：

 1. 在有网络的机器上下载二进制包
 2. 通过 U 盘或内部网络传输到目标服务器
 3. 按照上述步骤手动解压、注册、创建服务

 > 详情查看 **[配置文件说明](../04-操作指南/配置文件说明.md)** 了解更多配置项。
