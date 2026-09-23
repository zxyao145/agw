---
title: "多端客户端"
description: "通过浏览器、桌面和移动设备访问自己的 AGW 服务。"
weight: 60
lastmod: 2026-09-23
translationKey: docs/features/clients
---

## 在合适的设备上继续工作

AGW 提供 Web、Desktop 和 Mobile 客户端。连接同一个 Server 并使用相应身份后，可以访问自己有权限的项目和服务端保存的会话记录，按场景选择设备。

| 客户端 | 适合的场景 |
| --- | --- |
| Web | 无需安装桌面客户端，在浏览器中管理和对话 |
| Desktop | 日常工作空间、多个 Server 配置、本机或远程使用 |
| Mobile | 在移动设备上查看对话、访问项目并继续交流 |

![AGW Desktop 的对话工作空间；Web 与 Mobile 使用各自适配的界面。](/images/screenshots/desktop-chat.png)
{caption="AGW Desktop 的对话工作空间；Web 与 Mobile 使用各自适配的界面。"}

## 同一服务与不同服务的区别

在另一台设备上继续查看工作时，先连接同一个 Server，再选择同一个 Project 和会话。服务端保存的历史可以继续查看；尚未保存的实时输出可能需要等待写入或重新连接后确认。

不同 Server 分别保存自己的配置和记录。切换 Server 后看不到原项目时，先确认地址和身份，而不要立即重新创建项目。

## 开始使用

1. 完成 Server 初始化，确保设备能够访问服务地址。
2. Web 使用管理员密码或[第三方账号]({{< relref "/docs/features/oidc-login" >}})登录；Desktop 和 Mobile 使用 API Key 连接。
3. 确认 Server 和 Project，打开已有会话或创建新会话。
4. 检查历史记录和执行状态，避免因设备切换重复发起同一任务。

任务在实际执行主机上运行，手机或浏览器连接不会把执行环境搬到当前设备。Desktop Full 包含 Server，Desktop Client 连接已有 Server；Mobile 当前提供源码运行方式。不同客户端的布局与管理入口有所差异。

[查看客户端连接方法]({{< relref "/docs/guides/clients" >}}) · [安装与配置 Server]({{< relref "/docs/start/install" >}})
