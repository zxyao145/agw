---
title: "配置 Integrations"
description: "连接自己的外部服务账号，并授权 Agent 使用。"
weight: 100
lastmod: 2026-09-25
translationKey: docs/guides/integrations
---

集成让自定义 Agent 使用已授权的外部服务账号，例如读取 GitHub 中的资料。同一种服务可以配置多个账号，Agent 选择其中一个使用。当前内建目录提供 GitHub。

开始前，准备服务要求的认证资料，并确认该账号可以访问任务需要的资源。

## 选择服务并配置账号

- **Available integrations**：可供配置的全局目录定义。
- **Configured integrations**：当前用户配置好的账号或服务端点。
- **Connection**：实际选择和绑定的连接实例；同一集成可配置多个账号。

1. 在 Available integrations 的 GitHub 卡片上点击 **Configure**，完成所选认证方式要求的 setup。GitHub 的 OAuth 需要填写 OAuth App 的 Client ID 和 Client Secret；对话框中显示的 **OAuth callback URL** 需要登记到 GitHub 的 OAuth App 中。
2. 在目录卡片中对应认证方式的那一行点击 **New integration** 创建 Connection，填写 **Display name** 和清晰的 **Alias**。保存新的 OAuth Connection 后，AGW 会自动打开授权页面。
3. 确认连接状态为 Ready，再将具体连接绑定到 Agent 或 Project。连接卡片上的 **Authorize** 可以重新授权，**Validate** 可以重新检查连接。
4. 在自定义 Agent 的新回合执行一个读取操作，核对访问的是预期账号。

Alias 创建后不可修改，并在当前用户内唯一；只能使用小写字母、数字和单个连字符，最多 128 个字符，输入的大写字母会转为小写。Connection 工具名称使用 `{alias}__{operation}`，便于区分账号。GitHub 连接提供 `{alias}__current_user`、`{alias}__list_repositories` 和 `{alias}__clone_repository` 三个工具，分别用于读取当前账号、列出可访问的仓库，以及把仓库克隆到当前 Project 工作目录。

![AGW Desktop：Configured integrations 展示已配置的账号，Available integrations 展示可用的集成目录。](/images/screenshots/integrations.png)
{caption="AGW Desktop：Configured integrations 展示已配置的账号，Available integrations 展示可用的集成目录。"}

![AGW Desktop：创建 GitHub 集成的表单。认证方式由所点击的 New integration 那一行决定，表单中填写 Display name 和 Alias，保存后继续完成授权。](/images/screenshots/integration-create.png)
{caption="AGW Desktop：创建 GitHub 集成的表单。认证方式由所点击的 New integration 那一行决定，表单中填写 Display name 和 Alias，保存后继续完成授权。"}

## 所有权与凭据

安装设置和 Connection 都属于当前用户。修改接入设置后，当前用户的相关连接需要重新检查；其他用户的连接不受影响。Agent 只能使用属于当前用户且处于 Ready 状态的连接。凭据读取、OAuth 和工具调用都验证所有权。

## 当前限制

没有远程 Plugin Marketplace 的下载、签名或升级机制；不执行第三方 Plugin Skill 自带脚本；Connection 不注入任何 External Agent（Claude Code、Codex、Pi）。连接变化不应被理解为实时改写已创建的工具列表，修改后使用新回合验证。

## 实现与参考

- [Integrations](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/README.md)
