 # Apache 证书部署教程

 > Linux + Apache 环境下的证书自动部署

 ## 场景说明

 您的服务器运行 Apache（httpd 或 apache2）并托管了 `example.com` 网站，希望使用 CertGuard Agent 自动管理 SSL 证书。

 ## 前提条件

 - 已在服务器上安装 Apache（httpd 或 apache2）
 - Apache 已开启 SSL 模块（`a2enmod ssl` / 加载 mod_ssl）
 - 已完成 CertGuard Agent 安装

 ## 自动工作流程

 Agent 启动后会自动完成以下操作：

 ### 1. 自动检测 Apache

 检测到 `/usr/sbin/apache2` 或 `/usr/sbin/httpd`，自动选择 ApacheProvider（检测顺序：Nginx → Apache → IIS）。

 ### 2. 接收部署任务

 在 TOPSSL.CN 控制台为 `example.com` 申请证书后，Agent 接收到 `deploy_cert` 任务，任务中携带证书全部域名（SAN 列表，含通配符域名）与证书内容。

 ### 3. 证书写入

 Agent 将证书和私钥写入到：

 ```
 /etc/apache2/ssl/example.com/
 ├── certificate.crt  (证书文件)
 └── private.key      (私钥文件，权限 600)
 ```

 证书路径使用**绝对路径**，多个域名共用一个 SAN 证书时统一指向主域名目录（通配符条目排前时自动选用具体域名作目录名）。

 ### 4. 解析并修改 Apache 配置

 Agent 会递归解析 Apache 配置（`apache2.conf` / `httpd.conf` 及所有 Include 文件，支持 Debian 系 sites-enabled 符号链接），按域名**逐场景**处理：

 | 场景 | Agent 行为 |
|------|-----------|
| Apache 无该域名站点 | 跳过该域名；全部域名未匹配则任务失败上报 |
| 有域名且已配置证书（任何端口，含 443 与非标准端口） | 仅把 `SSLCertificateFile` / `SSLCertificateKeyFile` 替换为当前安装证书路径 |
| 有域名但只有 80 端口 | 基于 80 块自动创建 443 VirtualHost（端口转 443 + `SSLEngine on` + 证书两行，其余配置与 80 保持一致）；若全局没有 `Listen 443` 会自动补充 |

 域名匹配规则与 Nginx 完全一致：**精确匹配 + 通配符匹配**（双向生效，仅匹配一级子域），正则 `ServerName` / `ServerAlias` 不参与自动处理。

 新创建的 443 VirtualHost 示例（与 80 块配置保持一致）：

 ```apache
 <VirtualHost *:443>
     ServerName example.com

     DocumentRoot /var/www/example.com     # 原 80 块内容原样保留

     SSLEngine on
     SSLCertificateFile    /etc/apache2/ssl/example.com/certificate.crt
     SSLCertificateKeyFile /etc/apache2/ssl/example.com/private.key
 </VirtualHost>
 ```

 > 若 80 块同时监听 IPv6（`<VirtualHost [::]:80>`），会成对生成 `<VirtualHost [::]:443>`；创建 443 前置检测 mod_ssl 是否加载，缺失则跳过并提示。

 ### 5. 配置校验与回滚

 修改前 Agent 自动备份原配置文件（`/etc/apache2/ssl/.backup/`，保留最近 5 份），修改后执行 `apache2ctl configtest`（或 `httpd -t`）校验：

 - 校验通过 → `apache2ctl graceful` 优雅重载
 - 校验失败 → **自动回滚全部修改**，任务失败上报并附带错误信息，不影响现有服务

 ### 6. 上报结果

 执行结果上报到 TOPSSL.CN 平台，**上报的是实际匹配到的站点域名**（通配符证书部署 `*.example.com` 时上报 `test.example.com` 等具体站点名）。

 ## 需要手动处理的场景

 以下两种情况 Agent 无法安全地自动修改配置，会跳过并**在任务结果中提示**，需要您手动配置：

 ### 1. 站点有 443 监听但缺少 SSLCertificateFile

 现象：站点监听 443 但 VirtualHost 块内没有 `SSLCertificateFile` 指令（证书可能通过 `Include` 文件引入，Agent 为避免误改不会跟随 Include 解析）。

 解决办法：在该 VirtualHost 块内（或 Include 的配置文件中）添加：

 ```apache
 SSLEngine on
 SSLCertificateFile    /etc/apache2/ssl/example.com/certificate.crt
 SSLCertificateKeyFile /etc/apache2/ssl/example.com/private.key
 ```

 ### 2. 站点只有 80 端口且为纯重定向块

 现象：80 端口 VirtualHost 只有 `Redirect` / `RewriteRule` 跳转指令，没有 `DocumentRoot` / `ProxyPass` 等可服务内容。Agent 无法推断 443 应该提供什么内容，因此不自动创建。

 解决办法：手动为站点添加 443 VirtualHost，把原 80 块的内容（DocumentRoot / ProxyPass / location 配置等）复制到 443 块中，并加上 SSL 指令。

 ## 验证部署结果

 ### 查看 Agent 日志

 ```bash
 journalctl -u topssl-certguard-agent -n 20
 ```

 预期输出：

 ```
 [INF] 获取到 1 个任务
 [INF] 任务 #123 类型=deploy_cert
 [INF] Apache 证书已写入: example.com -> /etc/apache2/ssl/example.com
 [INF] Apache 解析到 3 个 VirtualHost，Listen 端口: 80
 [INF] 域名 example.com：已基于 /etc/apache2/sites-enabled/example.com.conf:5 创建 443 VirtualHost（补 Listen 443=True）
 [INF] Apache 配置校验通过（Syntax OK）
 [INF] Apache 重载完成
 [INF] 任务 #123: success
 ```

 ### 验证 SSL 连接

 ```bash
 openssl s_client -connect example.com:443 -servername example.com </dev/null 2>/dev/null | openssl x509 -noout -subject -dates
 ```

 ## 排错指南

 | 现象 | 可能原因 | 解决 |
|------|----------|------|
| Agent 未选择 Apache | Nginx 二进制同时存在 | Agent 按 Nginx→Apache 顺序检测，移除或重命名 Nginx 二进制 |
| 提示"Apache 未启用 mod_ssl" | 未加载 mod_ssl | 执行 `a2enmod ssl && systemctl reload apache2` |
| 提示"纯重定向块，无法自动创建 443" | 80 块只有跳转指令无内容 | 参考上文"需要手动处理的场景" |
| 提示"存在 443 监听但缺少 SSLCertificateFile" | 证书由 Include 文件引入 | 参考上文"需要手动处理的场景" |
| 配置校验失败且已回滚 | 修改后的配置语法错误 | 查看日志中 configtest 错误输出，按需手动修正 |
| 证书未生效 | 浏览器缓存 | 清除浏览器缓存或使用无痕模式 |

 > 更多问题请查看 **[常见问题](../06-常见问题/README.md)**。
