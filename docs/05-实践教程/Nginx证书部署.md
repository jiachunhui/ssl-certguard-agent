 # Nginx 证书部署教程

 > Linux + Nginx 环境下的证书自动部署

 ## 场景说明

 您的服务器运行 Nginx 并托管了 `example.com` 网站，希望使用 CertGuard Agent 自动管理 SSL 证书。

 ## 前提条件

 - 已在服务器上安装 Nginx（建议编译了 `--with-http_ssl_module` 模块）
 - Nginx 已配置好 `example.com` 的站点
 - 已完成 CertGuard Agent 安装（参考 [快速入门](../02-快速入门/README.md)）

 ## 自动工作流程

 Agent 启动后会自动完成以下操作：

 ### 1. 自动检测 Nginx

 Agent 启动时检测到 `/usr/sbin/nginx` 或 `/usr/bin/nginx` 可执行文件，自动选择 NginxProvider。

 ### 2. 接收部署任务

 在 TOPSSL.CN 控制台为 `example.com` 申请证书后，平台会下发 `deploy_cert` 任务，任务中携带证书全部域名（SAN 列表，含通配符域名）与证书内容。

 ### 3. 证书写入

 Agent 将证书和私钥写入到：

 ```
 /etc/nginx/ssl/example.com/
 ├── fullchain.pem  (证书文件)
 └── privkey.pem    (私钥文件，权限 600)
 ```

 证书路径使用**绝对路径**，多个域名共用一个 SAN 证书时统一指向主域名目录。

 ### 4. 解析并修改 Nginx 配置

 Agent 会递归解析 Nginx 配置（`nginx.conf` 及所有 include 文件，支持 Debian 系 sites-enabled 符号链接），按域名**逐场景**处理：

 | 场景 | Agent 行为 |
|------|-----------|
| Nginx 无该域名配置 | 跳过该域名；全部域名未匹配则任务失败上报 |
| 有域名且已配置证书（任何端口，含 443 与非标准端口） | 仅把 `ssl_certificate` / `ssl_certificate_key` 替换为当前安装证书路径 |
| 有域名但只有 80 端口 | 基于 80 块自动创建 443 配置（`listen 443 ssl;` + 证书两行，其余配置与 80 保持一致） |

 域名匹配规则：**精确匹配 + 通配符匹配**，双向生效（证书里的 `*.example.com` 可匹配配置中的 `www.example.com`，反之亦然）。通配符与 Nginx 语义一致，**仅匹配一级子域**（`*.example.com` 覆盖 `www.example.com`，不覆盖裸域 `example.com`，也不覆盖 `a.b.example.com`），兼容老式 `.example.com` 写法。正则 `server_name`（`~` 开头）不参与自动处理。

 > **已知限制**：`server_name` 指令跨行续写（分号才结束）的写法可能解析不到，该域名会被跳过（安全方向：宁可不部署也不误改），遇到这种情况请把 server_name 写在同一行。

 新创建的 443 配置示例（与 80 块配置保持一致）：

 ```nginx
 server {
     listen 443 ssl;
     server_name example.com;

     root /var/www/example;          # 原 80 块内容原样保留
     location / { ... }              # 原 80 块内容原样保留

     ssl_certificate     /etc/nginx/ssl/example.com/fullchain.pem;
     ssl_certificate_key /etc/nginx/ssl/example.com/privkey.pem;
 }
 ```

 > 为兼容不同 Nginx 版本，Agent 生成的新块只写最小必要指令（listen 443 ssl + 证书两行），不写 `http2`、`ssl_protocols` 等指令，相关设置继承 http 块全局配置；若 80 块同时监听 IPv6（`listen [::]:80`），会成对生成 `listen [::]:443 ssl`。

 ### 5. 配置校验与回滚

 修改前 Agent 自动备份原配置文件（`/etc/nginx/ssl/.backup/`，保留最近 5 份），修改后执行 `nginx -t` 校验：

 - 校验通过 → `nginx -s reload` 热重载
 - 校验失败 → **自动回滚全部修改**，任务失败上报并附带错误信息，不影响现有服务

 ### 6. 上报结果

 执行结果上报到 TOPSSL.CN 平台，您可以在控制台查看部署状态。

 ## 需要手动处理的场景

 以下两种情况 Agent 无法安全地自动修改配置，会跳过并**在任务结果中提示**，需要您手动配置：

 ### 1. 站点有 443 监听但缺少 ssl_certificate 指令

 现象：站点 `listen 443 ssl;` 但 server 块内没有 `ssl_certificate` 指令（证书可能通过 `include ssl.conf;` 之类的方式引入，Agent 为避免误改不会跟随 include 解析）。

 解决办法：在该 server 块内（或 include 的配置文件中）添加：

 ```nginx
 ssl_certificate     /etc/nginx/ssl/example.com/fullchain.pem;
 ssl_certificate_key /etc/nginx/ssl/example.com/privkey.pem;
 ```

 ### 2. 站点只有 80 端口且为纯重定向块

 现象：80 端口 server 块只有 `return 301 https://...` 或 `rewrite` 跳转指令，没有 `root` / `proxy_pass` / `location` 等可服务内容。Agent 无法推断 443 应该提供什么内容，强行复制会形成 HTTPS 死循环，因此不自动创建。

 解决办法：手动为站点添加 443 配置，例如：

 ```nginx
 server {
     listen 443 ssl;
     server_name example.com;

     # 从您的应用配置中复制原 80 块的内容（root / proxy_pass / location 等）
     root /var/www/example;

     ssl_certificate     /etc/nginx/ssl/example.com/fullchain.pem;
     ssl_certificate_key /etc/nginx/ssl/example.com/privkey.pem;
 }
 ```

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
 [INF] Nginx 解析到 3 个 server 块
 [INF] 域名 example.com：已基于 /etc/nginx/conf.d/example.conf:5 创建 443 配置
 [INF] nginx -t 校验通过
 [INF] Nginx 重载完成
 [INF] 任务 #123: success
 ```

 ### 验证证书

 ```bash
 openssl s_client -connect example.com:443 -servername example.com </dev/null 2>/dev/null | openssl x509 -noout -subject -dates
 ```

 ### 浏览器访问

 在浏览器中访问 `https://example.com`，确认证书有效且无安全警告。

 ## 排错指南

 | 现象 | 可能原因 | 解决 |
|------|----------|------|
| `nginx -t` 失败且 Agent 已回滚 | 修改后的配置语法错误（如 ssl 模块未编译） | 查看日志中的 nginx -t 错误输出，按需手动修正 |
| 提示"Nginx 未编译 ssl 模块" | 安装的 Nginx 缺少 `--with-http_ssl_module` | 重新编译 Nginx 或安装带 SSL 模块的版本 |
| 提示"纯重定向块，无法自动创建 443" | 80 块只有跳转指令无内容 | 参考上文"需要手动处理的场景" |
| 提示"存在 SSL 监听但缺少 ssl_certificate" | 证书由 include 文件引入 | 参考上文"需要手动处理的场景" |
| 证书写入失败 | 权限不足 | 确认 Agent 以 root 运行 |
| 证书未生效 | 浏览器缓存 | 清除浏览器缓存或使用无痕模式 |

 > 更多问题请查看 **[常见问题](../06-常见问题/README.md)**。
