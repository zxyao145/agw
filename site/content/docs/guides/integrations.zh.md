---
title: "配置 Integrations"
description: "连接自己的外部服务账号，并授权 Agent 使用。"
weight: 100
lastmod: 2026-09-15
translationKey: docs/guides/integrations
---

集成让自定义 Agent 使用已授权的外部服务账号，例如读取 GitHub 中的资料。同一种服务可以配置多个账号，Agent 选择其中一个使用。当前内建目录提供 GitHub。

开始前，准备服务要求的认证资料，并确认该账号可以访问任务需要的资源。

## 选择服务并配置账号

- **Available integrations**：可供配置的全局目录定义。
- **Configured integrations**：当前用户配置好的账号或服务端点。
- **Connection**：实际选择和绑定的连接实例；同一集成可配置多个账号。

1. 在 Available integrations 选择 GitHub，并完成所选认证方式要求的 setup。
2. 创建 Connection，填写清晰的 Alias，按配置要求完成认证。
3. 确认连接状态为 Ready，再将具体连接绑定到 Agent 或 Project。
4. 在自定义 Agent 的新回合执行一个读取操作，核对访问的是预期账号。

Alias 创建后不可修改，并在当前用户内唯一。Connection 工具名称使用 `{alias}__{operation}`，便于区分账号。

![AGW Desktop：Configured integrations 展示已配置的账号，Available integrations 展示可用的集成目录。](/images/screenshots/integrations.png)
{caption="AGW Desktop：Configured integrations 展示已配置的账号，Available integrations 展示可用的集成目录。"}

![AGW Desktop：创建 GitHub 集成的表单。选择认证方式、填写名称和 Alias，保存后继续完成授权。](/images/screenshots/integration-create.png)
{caption="AGW Desktop：创建 GitHub 集成的表单。选择认证方式、填写名称和 Alias，保存后继续完成授权。"}

## 所有权与凭据

安装设置和 Connection 都属于当前用户。修改接入设置后，当前用户的相关连接需要重新检查；其他用户的连接不受影响。Agent 只能使用属于当前用户且处于 Ready 状态的连接。凭据读取、OAuth 和工具调用都验证所有权。

## 当前限制

没有远程 Plugin Marketplace 的下载、签名或升级机制；不执行第三方 Plugin Skill 自带脚本；Connection 不注入外部 Codex 或 Claude Agent。连接变化不应被理解为实时改写已创建的工具列表，修改后使用新回合验证。

## 实现与参考

- [Integrations](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/README.md)
