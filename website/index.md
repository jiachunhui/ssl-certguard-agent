---
layout: home
description: 开源免费的 SSL 证书自动部署守护进程，配合 TOPSSL.CN 平台使用，自动完成 Nginx、Apache、IIS 的证书部署与续签，私钥不出服务器，证书永不过期。

hero:
  name: CertGuard Agent
  text: SSL 证书自动部署守护进程
  tagline: 私钥不出服务器 · 全自动部署与续签 · 开源可审计
  image:
    src: /logo.svg
    alt: CertGuard Agent
  actions:
    - theme: brand
      text: 🚀 快速开始
      link: /quickstart
    - theme: alt
      text: 项目概述
      link: /guide/
    - theme: alt
      text: GitHub
      link: https://github.com/jiachunhui/ssl-certguard-agent

features:
  - icon: 🔐
    title: 私钥不出服务器
    details: 密钥对在服务器本地生成与存储，平台只下发 CA 签名证书，私钥始终掌握在你自己手中
  - icon: ⚡
    title: 全自动部署
    details: 自动识别 Nginx / Apache / IIS，接收任务写入证书、校验配置并热重载服务
  - icon: 🔄
    title: 自动续签
    details: 周期心跳检查，证书到期前自动申请续签部署，让证书永不过期
  - icon: 🛡️
    title: 安全通信
    details: HMAC-SHA256 签名 + nonce 防重放 + 5 分钟时间窗口，通信全程可审计
  - icon: 📦
    title: 自动更新
    details: 心跳检测平台端新版本，自动下载升级包并替换，全程无需人工介入
  - icon: 🌍
    title: 代码开源
    details: Apache 2.0 许可证，可审计、可修改、可自行编译，杜绝供应链风险
---

<HomeContent />

## 什么是 CertGuard Agent？

CertGuard Agent 是一个**轻量级的后台守护进程**，安装在你运行 Web 服务的服务器上，自动完成 SSL 证书的部署和续签。它配合 [TOPSSL.CN](https://topssl.cn) 平台使用，让证书管理从手动操作变为全自动化。

**核心承诺：私钥不出服务器。** 所有密钥对的生成和存储完全在你的服务器本地完成，平台仅下发经过 CA 签名的证书内容和任务指令。

## 工作原理

![CertGuard Agent 工作原理图](/architecture.svg)

1. **自动发现** — Agent 启动后检测服务器上的 Web 服务（Nginx → Apache → IIS → 文件模式）
2. **安全注册** — 首次运行使用一次性令牌注册，之后全部使用 HMAC 签名通信
3. **周期心跳** — 每 60 秒发送心跳，同时拉取待执行的部署任务
4. **自动部署** — 收到 `deploy_cert` 任务后：写入证书 → 校验配置 → 重载 Web 服务 → 上报结果

## 支持的环境

| Web 服务 | 平台 | 状态 |
|----------|------|------|
| Nginx | Linux | ✅ 已支持，自动检测配置 |
| Apache | Linux | ✅ 已支持，自动检测配置 |
| IIS | Windows | ✅ 已支持，自动匹配站点绑定 |
| 通用文件模式 | 跨平台 | ✅ 已支持（兜底方案） |

## 下一步

- 🚀 [快速入门](./quickstart) — 30 秒让您的服务器接入自动部署
- 📖 [项目概述](./guide/) — 了解设计理念与安全架构
- 🛠️ [安装指南](./install/) — Linux / Windows 详细安装步骤
- ❓ [常见问题](./faq) — 高频问题与解决方案
