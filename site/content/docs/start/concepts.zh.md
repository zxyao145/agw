---
title: "核心概念"
description: "理解模型、Agent、Project、会话、工具、Agentflow 与 Job 如何配合。"
weight: 15
lastmod: 2026-09-25
translationKey: docs/start/concepts
---

在 AGW 中，模型负责推理，Agent 组织指令和能力，Project 提供工作空间。你可以通过 Chat 与 Agent 交互，也可以用 Agentflow 编排步骤、用 Job 安排执行时间。

## 模型与 Model Provider

模型相关配置分为三层：

| 概念 | 描述什么 |
| --- | --- |
| Provider | 模型服务的协议、地址和认证信息 |
| Model | 模型标识、上下文窗口和最大输出等规格 |
| Model Provider | 将一个模型与提供它的服务关联，供 Agent 选择 |

例如，你可以将同一个模型关联到不同的 Provider，再为 Agent 选择实际使用的连接。配置方法见[模型提供商]({{< relref "/docs/guides/providers" >}})。

## Agent：执行任务的角色

Agent 定义任务由谁完成、遵循什么指令，以及可以使用哪些能力。

- **自定义 Agent**：在 AGW 中配置模型、指令、工具和 Skills，由 AGW 运行。
- **外部 Agent**：接入 Claude Code、Codex、Pi 等 CLI，使用执行节点上已安装并配置好的工具环境。

Agent 定义可以用于多次任务；一次执行则是它处理某次输入的过程。例如，“代码解释助手”是 Agent，“解释这个函数”是一次输入。参见[创建自定义 Agent]({{< relref "/docs/guides/agents" >}})与[接入外部 Agent]({{< relref "/docs/guides/external-agents" >}})。

## Project：工作空间

Project 围绕一项工作组织目录、上下文与会话。例如，一个代码仓库可以对应一个 Project，其中分别保存代码阅读、问题排查等会话。

主工作目录 `Workspace` 是 Agent 默认的工作目录。附加目录允许访问更多服务端路径；在文件浏览器中切换目录，不会改变 Agent 的默认工作目录。

这些路径必须对 Server 或执行节点可见。详细说明见[Projects、文件与工作目录]({{< relref "/docs/guides/projects" >}})。

## Chat、会话与执行

**Chat** 是交互入口；**会话**保存连续交流的上下文与记录；**执行**是 Agent 或 Agentflow 处理输入的过程。

在一次会话中，你可以连续发送多条消息。执行过程中可以查看回复和工具活动，处理审批或补充信息请求。页面断开连接不等于执行已经停止，重新连接后应查看实际状态：会话列表中的图标会显示 **Running**（运行中）、**Last turn failed**（上一轮失败）或 **Last turn interrupted**（上一轮被中断）。

使用方法见[Chat 与执行记录]({{< relref "/docs/guides/chat" >}})。

## Agent Capability（Agent 能力）

Capability 是 Agent 完成任务时可以使用的能力。以下概念分别描述具体操作、工具组合、任务说明，以及外部服务的接入方式。

### Tools

Tool 是一项可调用的具体操作，例如读取文件或查询任务。Agent 根据任务需要调用工具，并使用返回的结果继续处理问题。

### ToolBlocks

ToolBlock 是需要整体选择和管理的一组关联 Tool，用来保持行为与状态一致。它按整组选用，模型仍逐个调用其中的 Tool。

例如，`todo` 组合了添加、列出和完成待办事项等工具。选择它后，模型可以调用 `todos_add`、`todos_get_all`、`todos_complete` 等成员；这些成员不能作为独立工具单独选用或移除。

### Skills

Skill 围绕任务提供使用说明、资源和可选工具，指导 Agent 如何完成工作。例如，`agw-job` 提供任务管理说明及工具。

Skill 分为 **Built-in（内置）**、**Local（本地）**和 **Remote（远程）**三类。Built-in 由 AGW 模块提供，例如上面的 `agw-job`；用户可以添加 Local 或 Remote Skill：Local 上传到 AGW 服务端，Remote 从指定网址读取。根据内容由谁维护、是否需要随包提供资源来选择，具体格式和更新规则见 [Tools 与 Skills]({{< relref "/docs/guides/tools-skills" >}})。

### MCP

MCP 是连接工具服务的协议。配置 MCP Server 后，AGW 可以从服务中发现并调用工具，让 Agent 使用该服务提供的能力。配置方法见 [MCP 服务]({{< relref "/docs/guides/mcp" >}})。

### Plugins

Plugin 定义一个集成提供哪些能力，以及如何接入服务，包括连接方式、认证方式、工具来源和内置 Skills。例如，GitHub Plugin 定义 GitHub 的认证与工具能力。

### Integrations

Integrations 是用户选择和配置外部服务的入口，分为两部分：

- **Available integrations（可用集成）**：可供选择和配置的集成目录，例如 GitHub。目录展示 Plugin 定义的能力。
- **Configured integrations（已配置的集成）**：用户配置好的具体账号或服务端点。同一种集成可以配置多个账号，例如个人和工作 GitHub 账号。

Agent 选择具体的已配置集成，只有归属匹配且处于 Ready 状态的账号或端点才能提供能力。已配置集成在代码中对应 `Connection` 类型。

配置方法见 [Integrations]({{< relref "/docs/guides/integrations" >}})。

![AGW Desktop：Configured integrations 展示已配置的账号，Available integrations 展示可用的集成目录。](/images/screenshots/integrations.png)
{caption="AGW Desktop：Configured integrations 展示已配置的账号，Available integrations 展示可用的集成目录。"}

## Agentflow 与 Job

**Agentflow 决定步骤如何协作**。它可以组合多个 Agent，以及分支、并行、人工审批等节点。例如，让一个 Agent 收集材料，另一个 Agent 整理摘要，再由人确认输出。

**Job 决定什么时候运行**。它按一次性、间隔或 Cron 触发一个 Agent 或 Agentflow，并记录每次尝试的结果。一个简单的定时问答任务可以直接选择 Agent；需要多个步骤时再选择 Agentflow。

二者可以独立使用：Agentflow 可以在 Chat 中手动运行，Job 也可以只执行一个 Agent。参见[Agentflows]({{< relref "/docs/guides/agentflows" >}})与[Jobs]({{< relref "/docs/guides/jobs" >}})。

## 把概念串起来

以定期整理项目进展为例：

1. 创建 Project，设置项目的工作目录。
2. 配置 Model Provider，创建负责整理进展的 Agent。
3. 为 Agent 绑定读取资料需要的工具或已配置的集成。
4. 在 Chat 中运行一次，确认结果符合要求。
5. 如需分工或审批，用 Agentflow 组织步骤；如需定期执行，用 Job 设置时间。

第一次使用时，先完成[一次简单对话]({{< relref "/docs/start/first-chat" >}})，再按任务需要增加其他能力。
