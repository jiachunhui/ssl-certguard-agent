 # Nginx 证书部署教程

 > Linux + Nginx 环境下的证书自动部署

 ## 场景说明

 您的服务器运行 Nginx 并托管了 `example.com` 网站，希望使用 CertGuard Agent 自动管理 SSL 证书。

 ## 前提条件

- 已在服务器上安装 Nginx
- Nginx 已配置好 `example.com` 的 HTTP 站点
- 已完成 CertGuard Agent 安装（参考 [快速入门](../02-快速入门/README.md)）

 ## 自动工作流程

 Agent 启动后会自动完成以下操作：

 ### 1. 自动检测 Nginx

 Agent 启动时检测到 `/usr/sbin/nginx` 或 `/usr/bin/nginx` 可执行文件，自动选择 NginxProvider。

 ### 2. 接收部署任务

 在 TOPSSL.CN 控制台为 `example.com` 申请证书后，平台会下发 `deploy_cert` 任务。

 ### 3. 证书写入

 Agent 将证书和私钥写入到：

 ```
 /etc/nginx/ssl/example.com/
 ├── fullchain.pem  (证书文件)
 └── privkey.pem    (私钥文件，权限 600)
 ```

 ### 4. 配置检查

 Agent 自动执行 `nginx -t` 验证配置正确性，确保不会因为错误的配置导致服务中断。

 ### 5. 热重载 Nginx

 配置检查通过后，执行 `nginx -s reload` 热重载，无中断地应用新证书。

 ### 6. 上报结果

 执行结果上报到 TOPSSL.CN 平台，您可以在控制台查看部署状态。

 ## Nginx 配置要求

 如果您需要手动参考 Agent 生成的证书路径，为 Nginx 站点配置 SSL：

 ```nginx
 server {
     listen 443 ssl;
     server_name example.com;

     ssl_certificate     /etc/nginx/ssl/example.com/fullchain.pem;
     ssl_certificate_key /etc/nginx/ssl/example.com/privkey.pem;

     ssl_protocols TLSv1.2 TLSv1.3;
     ssl_ciphers HIGH:!aNULL:!MD5;

     location / {
         proxy_pass http://localhost:3000;
     }
 }
 ```

 > **注意**：Agent 只负责写入证书文件和重载 Nginx，不修改 Nginx 配置文件。您需要确保 Nginx 配置中正确引用了证书路径。

 ## 验证部署结果

 ### 查看 Agent 日志

 ```bash
 journalctl -u topssl-certguard-agent -n 20
 ```

 预期输出：

 ```
 [INF] 获取到 1 个任务
 [INF] 任务 #123 类型=deploy_cert
 [INF] Nginx 证书已写入: example.com -> /etc/nginx/ssl/example.com
 [INF] Nginx 重载完成
 [INF] 任务 #123: success
 ```

 ### 验证证书

 ```bash
 openssl s_client -connect example.com:443 -servername example.com </dev/null 2>/dev/null | openssl x509 -noout -subject -dates
 ```

 > ![截图] 此处占位：终端输出 openssl 证书验证结果的截图

 ### 浏览器访问

 在浏览器中访问 `https://example.com`，确认证书有效且无安全警告。

 > ![截图] 此处占位：浏览器地址栏显示安全锁的截图

 ## 排错指南

 | 现象 | 可能原因 | 解决 |
|------|----------|------|
| `nginx -t` 失败 | nginx.conf 引用了不存在的证书路径 | 检查 SSL 配置路径是否正确 |
| 证书写入失败 | 权限不足 | 确认 Agent 以 root 运行 |
| Nginx 重载失败 | 配置文件语法错误 | 手动执行 `nginx -t` 检查 |
| 证书未生效 | 浏览器缓存 | 清除浏览器缓存或使用无痕模式 |

 > 更多问题请查看 **[常见问题](../06-常见问题/README.md)**。
