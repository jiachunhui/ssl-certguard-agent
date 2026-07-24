 # Apache 证书部署教程

 > Linux + Apache 环境下的证书自动部署

 ## 场景说明

 您的服务器运行 Apache（httpd）并托管了 `example.com` 网站，希望使用 CertGuard Agent 自动管理 SSL 证书。

 ## 前提条件

- 已在服务器上安装 Apache（httpd 或 apache2）
- Apache 已开启 SSL 模块（`a2enmod ssl`）
- 已完成 CertGuard Agent 安装

 ## 工作流程

 Agent 启动后会自动完成以下操作：

 ### 1. 自动检测 Apache

 检测到 `/usr/sbin/apache2` 或 `/usr/sbin/httpd`，自动选择 ApacheProvider。

 ### 2. 接收部署任务

 在 TOPSSL.CN 控制台为 `example.com` 申请证书后，Agent 接收到 `deploy_cert` 任务。

 ### 3. 证书写入

 Agent 将证书和私钥写入到：

 ```
 /etc/apache2/ssl/example.com/
 ├── certificate.crt  (证书文件)
 └── private.key      (私钥文件)
 ```

 ### 4. 优雅重载 Apache

 Agent 执行 `apache2ctl graceful` 优雅重载，不中断现有连接。

 如果 `apache2ctl` 不可用，自动尝试 `httpd -k graceful`。

 ### 5. 上报结果

 执行结果上报到 TOPSSL.CN 平台。

 ## Apache 配置要求

 确保 Apache 配置中引用了正确的证书路径：

 ```apache
 <VirtualHost *:443>
     ServerName example.com

     SSLEngine on
     SSLCertificateFile      /etc/apache2/ssl/example.com/certificate.crt
     SSLCertificateKeyFile   /etc/apache2/ssl/example.com/private.key

     # 如果 CA 证书链是独立的文件
     # SSLCertificateChainFile /etc/apache2/ssl/example.com/chain.crt

     DocumentRoot /var/www/example.com
 </VirtualHost>
 ```

 ### 启用 SSL 站点

 ```bash
 a2enmod ssl
 a2ensite example.com-ssl
 systemctl reload apache2
 ```

 ## 验证部署结果

 ### 查看 Agent 日志

 ```bash
 journalctl -u topssl-certguard-agent -n 20
 ```

 预期输出：

 ```
 [INF] Apache 证书已写入: example.com
 [INF] Apache 重载完成
 [INF] 任务 #123: success
 ```

 > ![截图] 此处占位：终端显示 Agent 日志的截图，展示 Apache 部署成功输出

 ### 验证 SSL 连接

 ```bash
 openssl s_client -connect example.com:443 -servername example.com </dev/null 2>/dev/null | openssl x509 -noout -subject -dates
 ```

 ## 排错指南

 | 现象 | 可能原因 | 解决 |
|------|----------|------|
| Agent 未选择 Apache | Nginx 二进制同时存在 | Agent 按 Nginx→Apache 顺序检测，移除或重命名 Nginx 二进制 |
| 证书路径不匹配 | 手动配置的路径与 Agent 写入路径不同 | 修改 Apache 配置引用 Agent 的写入路径 |
| 重载失败 | Apache 配置语法错误 | 执行 `apache2ctl configtest` 检查 |
| SSL 模块未启用 | `a2enmod ssl` 未执行 | 执行 `a2enmod ssl && systemctl reload apache2` |

 > 更多问题请查看 **[常见问题](../06-常见问题/README.md)**。
