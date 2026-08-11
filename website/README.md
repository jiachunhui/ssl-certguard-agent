# CertGuard Agent 官方网站

本站基于 **VitePress**（Vue3 + Vite + Markdown 静态站点生成器）构建，与 [certd.docmirror.cn](https://certd.docmirror.cn) 同款技术栈，用于项目介绍与使用文档。

## 技术要点

| 项目 | 说明 |
|------|------|
| 框架 | VitePress 1.x（Vue 3 + Vite） |
| 内容格式 | Markdown（含 frontmatter SEO 元数据） |
| 渲染方式 | SSG 静态预渲染，首屏即完整 HTML，对搜索引擎友好 |
| SEO | 全站 title/description/keywords、Open Graph、Twitter Card、canonical、sitemap.xml、robots.txt、JSON-LD 结构化数据 |

## 本地开发

```bash
npm install
npm run dev        # 本地预览 http://localhost:5173
npm run build      # 构建静态站点，输出到 .vitepress/dist/
npm run preview    # 预览构建产物
```

## 目录结构

```
website/
├── index.md                  # 首页（hero + 六大特性 + 工作原理）
├── quickstart.md             # 快速入门
├── faq.md                    # 常见问题
├── support.md                # 服务支持
├── guide/                    # 概述（项目概述 / 为什么开源 / 安全架构）
├── install/                  # 安装指南（系统要求 / Linux / Windows / 服务管理）
├── ops/                      # 操作指南（命令行 / 配置 / 日志 / 更新卸载）
├── tutorial/                 # 实践教程（Nginx / Apache / IIS / 多域名）
├── dev/                      # 开发指南（构建发布 / 贡献指南）
├── .vitepress/
│   ├── config.ts             # 站点配置（SEO、导航、侧边栏、搜索）
│   └── theme/                # 主题定制（品牌色）
├── public/                   # 静态资源（logo、robots.txt）
└── _build/                   # 文档迁移脚本（一次性使用，可删除）
```

## 内容维护

内容源为仓库根目录 `docs/`（GitHub 上直接浏览的文档）。网站内容由 `_build/prepare_docs.py` 从 `docs/` 迁移生成（自动补充 SEO frontmatter、修正链接、清理截图占位符）。

修改文档时**优先修改 `docs/` 下的源文件**，然后运行迁移脚本同步到网站：

```bash
python _build/prepare_docs.py
```

> 注意：迁移脚本会**覆盖** `website/` 下对应文件，网站侧的手工修改会被冲掉。

## 部署前必改项

1. **域名**：`.vitepress/config.ts` 中 `siteUrl`（当前为 `https://certguard.topssl.cn` 占位），影响 canonical / sitemap / OG 标签
2. **搜索引擎验证**：在 `config.ts` 的 `head` 中添加 Google/Bing 站点验证 meta
3. **构建产物**：`.vitepress/dist/` 可部署到任意静态托管（Nginx / 腾讯云 COS / GitHub Pages 等）
4. 建议在 `public/` 放置 `og-cover.png`（1200×630）作为社交分享图
