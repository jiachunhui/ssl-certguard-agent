import { defineConfig } from 'vitepress'

// 站点基础 URL（部署后按实际域名修改，同时影响 canonical / sitemap / OG）
const siteUrl = 'https://certguard.topssl.cn'

const seoDescription =
  'CertGuard Agent 是开源免费的 SSL 证书自动部署守护进程，配合 TOPSSL.CN 平台使用，自动完成 Nginx、Apache、IIS 的证书部署与续签，私钥不出服务器。'

export default defineConfig({
  lang: 'zh-CN',
  title: 'CertGuard Agent',
  titleTemplate: ':title - SSL 证书自动部署守护进程',
  description: seoDescription,

  // ============ SEO：全站 head 注入 ============
  head: [
    // 基础 SEO
    ['meta', { name: 'keywords', content: 'SSL证书自动部署,证书自动续签,证书自动更新,CertGuard Agent,证书管理工具,Nginx证书部署,Apache证书部署,IIS证书部署,https证书,通配符证书,TLS证书,TOPSSL' }],
    ['meta', { name: 'author', content: 'TOPSSL.CN' }],
    ['meta', { name: 'robots', content: 'index, follow' }],
    ['meta', { name: 'theme-color', content: '#2563eb' }],

    // Canonical（防重复收录，指向主域名）
    ['link', { rel: 'canonical', href: siteUrl }],

    // 站点图标
    ['link', { rel: 'icon', href: '/logo.svg', type: 'image/svg+xml' }],
    ['link', { rel: 'apple-touch-icon', href: '/logo.svg' }],

    // Open Graph（微信 / Facebook / 搜索引擎社交摘要）
    ['meta', { property: 'og:type', content: 'website' }],
    ['meta', { property: 'og:site_name', content: 'CertGuard Agent' }],
    ['meta', { property: 'og:title', content: 'CertGuard Agent - SSL 证书自动部署守护进程' }],
    ['meta', { property: 'og:description', content: seoDescription }],
    ['meta', { property: 'og:url', content: siteUrl }],
    ['meta', { property: 'og:image', content: siteUrl + '/og-cover.png' }],
    ['meta', { property: 'og:locale', content: 'zh_CN' }],

    // Twitter Card
    ['meta', { name: 'twitter:card', content: 'summary_large_image' }],
    ['meta', { name: 'twitter:title', content: 'CertGuard Agent - SSL 证书自动部署守护进程' }],
    ['meta', { name: 'twitter:description', content: seoDescription }],
    ['meta', { name: 'twitter:image', content: siteUrl + '/og-cover.png' }],

    // 结构化数据：软件应用（JSON-LD），提升搜索引擎富摘要展示
    [
      'script',
      { type: 'application/ld+json' },
      JSON.stringify({
        '@context': 'https://schema.org',
        '@type': 'SoftwareApplication',
        name: 'CertGuard Agent',
        alternateName: 'CertGuard',
        description: seoDescription,
        applicationCategory: 'DeveloperApplication',
        operatingSystem: 'Linux, Windows',
        license: 'https://www.apache.org/licenses/LICENSE-2.0',
        url: siteUrl,
        inLanguage: 'zh-CN',
        offers: { '@type': 'Offer', price: '0', priceCurrency: 'CNY' },
        publisher: { '@type': 'Organization', name: 'TOPSSL.CN', url: 'https://topssl.cn' },
      }),
    ],
  ],

  // ============ SEO：sitemap 自动生成 ============
  sitemap: {
    hostname: siteUrl,
  },

  // 清理 URL 里的 .html 后缀，输出纯路径（/quickstart 而非 /quickstart.html）
  cleanUrls: true,
  lastUpdated: true,

  // 排除不作为页面的 markdown（如开发者说明 README.md）
  srcExclude: ['README.md'],

  markdown: {
    lineNumbers: false,
    image: { lazyLoading: true },
  },

  themeConfig: {
    logo: '/logo.svg',
    siteTitle: 'CertGuard Agent',

    // 顶部导航
    nav: [
      { text: '首页', link: '/' },
      { text: '指南', link: '/guide/', activeMatch: '/guide/' },
      { text: '快速入门', link: '/quickstart' },
      { text: '安装', link: '/install/', activeMatch: '/install/' },
      { text: '操作', link: '/ops/', activeMatch: '/ops/' },
      { text: '教程', link: '/tutorial/', activeMatch: '/tutorial/' },
      { text: 'FAQ', link: '/faq' },
      { text: '开发', link: '/dev/', activeMatch: '/dev/' },
    ],

    // 侧边栏
    sidebar: {
      '/guide/': [
        {
          text: '开始了解',
          items: [
            { text: '项目概述', link: '/guide/' },
            { text: '为什么开源', link: '/guide/why-open-source' },
            { text: '安全架构', link: '/guide/security' },
          ],
        },
      ],
      '/install/': [
        {
          text: '安装指南',
          items: [
            { text: '安装概览', link: '/install/' },
            { text: '系统要求', link: '/install/requirements' },
            { text: 'Linux 手动安装', link: '/install/linux' },
            { text: 'Windows 手动安装', link: '/install/windows' },
            { text: '服务管理', link: '/install/service' },
          ],
        },
      ],
      '/ops/': [
        {
          text: '操作指南',
          items: [
            { text: '操作概览', link: '/ops/' },
            { text: '命令行参考', link: '/ops/cli' },
            { text: '配置文件说明', link: '/ops/config' },
            { text: '日志管理', link: '/ops/logs' },
            { text: '更新与卸载', link: '/ops/update-uninstall' },
          ],
        },
      ],
      '/tutorial/': [
        {
          text: '实践教程',
          items: [
            { text: '教程概览', link: '/tutorial/' },
            { text: 'Nginx 证书部署', link: '/tutorial/nginx' },
            { text: 'Apache 证书部署', link: '/tutorial/apache' },
            { text: 'IIS 证书部署', link: '/tutorial/iis' },
            { text: '多域名部署', link: '/tutorial/multi-domain' },
          ],
        },
      ],
      '/dev/': [
        {
          text: '开发指南',
          items: [
            { text: '开发概览', link: '/dev/' },
            { text: '构建发布', link: '/dev/build' },
            { text: '贡献指南', link: '/dev/contribute' },
          ],
        },
      ],
    },

    // 页脚
    footer: {
      message: 'Apache License 2.0 开源 · 私钥不出服务器 · 证书永不过期',
      copyright: 'Copyright © 2026 TOPSSL.CN · <a href="https://github.com/jiachunhui/ssl-certguard-agent">GitHub</a>',
    },

    // 文档增强
    editLink: {
      pattern: 'https://github.com/jiachunhui/ssl-certguard-agent/edit/main/docs/:path',
      text: '在 GitHub 上编辑此页',
    },
    lastUpdated: {
      text: '最后更新于',
      formatOptions: { dateStyle: 'medium', timeStyle: 'short' },
    },
    docFooter: { prev: '上一页', next: '下一页' },
    outline: { level: [2, 3], label: '本页目录' },
    darkModeSwitchLabel: '外观',
    sidebarMenuLabel: '菜单',
    returnToTopLabel: '回到顶部',
    externalLinkIcon: true,

    // 站内搜索（SSG 输出索引，离线可用，利于本地检索体验）
    search: {
      provider: 'local',
      options: {
        translations: {
          button: { buttonText: '搜索文档', buttonAriaLabel: '搜索文档' },
          modal: {
            noResultsText: '未找到相关结果',
            resetButtonTitle: '清除查询条件',
            footer: { selectText: '选择', navigateText: '切换', closeText: '关闭' },
          },
        },
      },
    },

    socialLinks: [{ icon: 'github', link: 'https://github.com/jiachunhui/ssl-certguard-agent' }],
  },
})
