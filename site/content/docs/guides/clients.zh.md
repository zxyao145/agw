---
title: "Web、Desktop 与 Mobile"
description: "选择客户端，连接正确的 Server 并管理会话。"
weight: 110
lastmod: 2026-09-15
translationKey: docs/guides/clients
---

前提：Server 已完成初始化。客户端不会替代服务端的模型、文件或执行环境。

| 客户端 | 连接方式 | 适用场景 |
| --- | --- | --- |
| Web | 远程管理员 Cookie；同源 API 访问 | 浏览器管理与对话 |
| Desktop | 命名 Bearer Token，支持多个 Server profile | 本机或远程的日常工作空间 |
| Mobile | 命名 Bearer Token | 移动设备上的对话与项目访问 |

## 使用步骤

1. Web 打开 Server 地址；源码开发时打开端口 `3001`。
2. Desktop Full 可使用内置 Server；Client 配置已有 Server 的地址和 Token。
3. Mobile 配置可从设备访问的 Server 地址与 Token；设备上的 localhost 通常不是开发电脑。
4. 创建一个短会话，确认连接目标、Project 与历史记录。

Desktop 以 Chat 为主界面，Projects 和其他管理入口在 Settings 中。Server profile 切换使用独立缓存，修改地址或 Token 后，会清除旧连接使用的缓存，避免混入其他 Server 的数据。

![AGW Desktop 对话界面：顶部选择 Project 和 Agent，底部输入消息。](/images/screenshots/desktop-chat.png)
{caption="AGW Desktop 对话界面：顶部选择 Project 和 Agent，底部输入消息。"}

## 运行差异

Desktop renderer 自用端口 `3000`，不依赖 Web 开发服务器。Full 的 Server 守护进程在关闭桌面窗口后仍继续运行；默认关闭窗口会缩到托盘。

Mobile 使用 Expo，原生工程由 CNG 生成。当前文档提供[源码运行入口]({{< relref "/docs/development/setup" >}})，不假设存在应用商店安装包。远程访问建议使用 HTTPS，确保代理允许执行所需的 WebSocket。

## 实现与参考

- [Desktop](https://github.com/zxyao145/agw/blob/main/src/clients/desktop/README.md)
- [Mobile](https://github.com/zxyao145/agw/blob/main/src/clients/mobile/README.md)
