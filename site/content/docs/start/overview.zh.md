---
title: "AGW 是什么"
description: "理解 Agent、Project、Chat、Agentflow 与 Job 的关系。"
weight: 10
lastmod: 2026-09-23
translationKey: docs/start/overview
---

AGW 是面向个人和小型研发团队的自托管 Agent 工作平台，也可作为 Agent 网关提供服务。它将自定义 Agent、Claude Code、Codex 和 Pi 等外部 Agent 放在统一界面中，围绕项目保留对话与执行记录。

![AGW Desktop 对话界面：顶部选择 Project 和 Agent，底部输入消息。](/images/screenshots/desktop-chat.png)
{caption="AGW Desktop 对话界面：顶部选择 Project 和 Agent，底部输入消息。"}

## 核心概念

| 概念 | 用途 |
| --- | --- |
| Model Provider | 连接提供商与模型，指定实际使用的模型配置 |
| Agent | 配置指令、模型和能力，或接入已有外部 Agent |
| Project | 保存工作目录、上下文与会话的任务空间 |
| Chat | 发起交互式执行，查看消息、工具活动并处理人工输入 |
| Agentflow | 将多个节点连接成可执行流程 |
| Job | 按一次性、间隔或 Cron 触发 Agent 或 Agentflow |

各概念的区别及配合方式见[核心概念]({{< relref "/docs/start/concepts" >}})。

## 可以用 AGW 做什么

假设正在维护一个代码项目：可以先让 Agent 解释陌生代码，再让另一个 Agent 审查修改；讨论和结果保存在项目的会话中。需要重复完成的工作，例如每周整理进展，可以在手动验证后设为定时任务。

| 需求 | 从哪里开始 |
| --- | --- |
| 问问题、整理一段文字 | 创建 Agent，在 Chat 中发送任务 |
| 阅读或修改项目文件 | 设置 Project 工作目录，再为 Agent 配置相应工具 |
| 让不同 Agent 接着讨论 | 在同一会话中切换 Agent，说明下一步任务 |
| 按固定步骤协作 | 用 Agentflow 连接各个步骤，按需加入人工确认 |
| 定期重复一项工作 | 用 Job 设置时间，并查看每次执行结果 |

## 任务在哪里运行

**Server（服务端）**是实际运行 AGW 的程序。Web、Desktop 和 Mobile 是操作它的客户端。使用远程 Server 时，Agent 访问的是远程主机上的文件、命令和工具；在手机中打开会话，不会让任务改在手机上运行。

首次在自己的电脑上使用，可以选择包含 Server 的 Desktop Full。希望通过浏览器使用，可以部署 Docker 或 Portable Server。模型服务需要另外配置；选择远程模型时，发送给模型的内容会离开 AGW 所在主机。

## 选择部署方式

**Standalone（单机）**把管理页面、对话执行和定时任务放在一个 Server 中，默认使用 SQLite 数据库，适合本机使用和单台主机部署。

**Control/Data Plane 分离部署**把管理与调度交给控制面，把实际执行交给数据面。它适合需要分别部署管理服务、增加执行节点的场景，但也需要共享 PostgreSQL、密钥和工作目录，并配置请求转发。

通常可以先从 Standalone 开始。安装选项见[安装与配置 Server]({{< relref "/docs/start/install" >}})；需要分离部署时，再阅读[部署步骤与路由配置]({{< relref "/docs/operations/split" >}})。

## 从一个小任务开始

1. [安装与配置 Server]({{< relref "/docs/start/install" >}})并初始化服务。
2. 配置一个可用模型，创建一个 Agent。
3. 在 Chat 发送简单问题，确认模型和执行链路正常。
4. 有文件需求时创建 Project；有固定步骤时再使用 Agentflow；有周期需求时再配置 Job。

执行记录保存在你的服务数据库中。自托管不代表所有推理都发生在本机：选择远程模型提供商时，请求仍会发送到该提供商。

## 当前边界

AGW 尚未达到 1.0。它适合清晰、可拆分的任务与人机协作；复杂任务仍需要清楚的输入、完成标准和人工检查。当前使用管理员登录、第三方账号登录及 API Key，尚不提供角色管理或按 API Key 分配权限范围。

## 实现与参考

- [Product overview](https://github.com/zxyao145/agw/blob/main/README.md)
- [Authentication](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Auth/README.md)

- [Host composition](https://github.com/zxyao145/agw/blob/main/src/server/Agw.ControlPlane.Host/ControlPlaneHostModule.cs)
- [Execution routes](https://github.com/zxyao145/agw/blob/main/src/server/Agw.DataPlane.Host/DataPlaneHostModule.cs)
- [Ingress routing](https://github.com/zxyao145/agw/blob/main/deploy/nginx.split.conf.example)
- [Execution providers](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
