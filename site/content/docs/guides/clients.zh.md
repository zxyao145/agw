---
title: "Web、Desktop 与 Mobile"
description: "选择客户端，连接正确的 Server 并管理会话。"
weight: 110
lastmod: 2026-09-25
translationKey: docs/guides/clients
---

前提：Server 已完成初始化。客户端不会替代服务端的模型、文件或执行环境。

| 客户端 | 连接方式 | 适用场景 |
| --- | --- | --- |
| Web | 管理员密码或第三方账号登录得到的会话 Cookie；同源 API 访问 | 浏览器管理与对话 |
| Desktop | API Key，可手动填写或由第三方账号登录签发，支持多个 Server profile | 本机或远程的日常工作空间 |
| Mobile | API Key，可手动填写或导入 Web 生成的连接配置，支持多个 Server profile | 移动设备上的对话与项目访问 |

## 使用步骤

1. Web 打开 Server 地址；源码开发时打开端口 `3001`。
2. Desktop Full 可使用内置 Server。Client 在 **Settings → Connections & app** 中点击 **+**（Add remote Server），填写 **Name**、**Server URL** 和 **API token**。远程 Server 使用 `http://` 地址时，需要勾选风险确认，说明 API token 和通信内容会在网络上以明文传输。
3. Mobile 配置可从设备访问的 Server 地址与 API Key；设备上的 localhost 通常不是开发电脑。也可以在 Web 的 **Settings → Server access** 创建 API Key 后点击 **Copy config**，再在 Mobile 的 **Import Web configuration** 中粘贴。删除 Mobile 上的 profile 不会撤销 Server 端的 API Key，需要在 Web 中撤销。
4. 创建一个短会话，确认连接目标、Project 与历史记录。

Desktop 以 Chat 为主界面，Projects 和其他管理入口在 Settings 中。Server profile 切换使用独立缓存，修改地址或 API Key 后，会清除旧连接使用的缓存，避免混入其他 Server 的数据。

![AGW Desktop 对话界面：选择 Project 和 Agent 后输入消息。](/images/screenshots/desktop-chat.png)
{caption="AGW Desktop 对话界面：选择 Project 和 Agent 后输入消息。"}

## 第三方账号登录

Server 启用身份提供商后，Web 登录页会显示对应的账号按钮，选择后完成验证即可回到原来要访问的页面。Desktop 在 Server 配置中显示“Sign in with …”，系统浏览器完成验证后自动获得 API Key，无需手动粘贴；同一处提供“Sign out”撤销该 API Key。内置的本机 Server profile 处于第三方账号登录模式、且没有可用 API Key 时（例如退出登录或 API Key 过期后），还会显示“Use local administrator”。

Mobile 不支持第三方账号登录，使用手动填写或导入的 API Key。每个第三方账号是一个独立用户，数据与管理员账号分开。配置方法见[配置与认证]({{< relref "/docs/operations/configuration" >}})，行为说明见[第三方账号登录]({{< relref "/docs/features/oidc-login" >}})。

## 运行差异

Desktop renderer 自用端口 `3000`，不依赖 Web 开发服务器。Full 的 Server 守护进程在关闭桌面窗口后仍继续运行；默认关闭窗口会缩到托盘。

Mobile 使用 Expo，原生工程由 CNG 生成。当前文档提供[源码运行入口]({{< relref "/docs/development/setup" >}})，不假设存在应用商店安装包。远程访问建议使用 HTTPS，确保代理允许执行所需的 WebSocket。

## 实现与参考

- [Desktop](https://github.com/zxyao145/agw/blob/main/src/clients/desktop/README.md)
- [Mobile](https://github.com/zxyao145/agw/blob/main/src/clients/mobile/README.md)
