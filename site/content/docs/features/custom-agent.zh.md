---
title: "自定义 Agent"
description: "组合模型、指令与工具，让 Agent 按明确职责判断和执行。"
weight: 1
lastmod: 2026-09-25
translationKey: docs/features/custom-agent
---

## 为任务定义自己的 Agent

在 AGW 中，你可以把模型、指令和工具组合成一个可复用的 Agent。它根据任务和上下文作出判断，选择可用工具执行操作，再根据结果继续处理。

例如，创建一个“文档审查助手”，让它阅读材料、判断哪些地方难懂，并给出修改建议。需要直接修改文件时，再为它配置相应工具和权限。

## 可以自定义什么

| 配置 | 决定什么 |
| --- | --- |
| Model Provider（模型提供商） | 使用哪个模型理解任务和生成回复 |
| 指令 | 职责、任务范围、处理要求与输出格式 |
| Tools 与 Skills | 可调用的操作，以及任务所需的指导与能力 |
| 配置好的集成连接 | 可访问的外部服务或账号 |
| Response Schema | 回复是否按 JSON Schema 返回结构化数据，详见 [JSON Schema 结构化响应]({{< relref "/docs/features/structured-output" >}}) |

你可以为材料整理、代码解释和结果审查分别创建 Agent，在 Chat 中按任务选择，也可以把它们放进 Agentflow，作为流程中的执行步骤。

## 开始使用

1. 准备可用的 Model Provider，在 Agents 中创建自定义 Agent。
2. 选择模型，写清职责和输出要求，例如“审查文档，按原文、问题、建议改写列出结果”。
3. 按需添加工具、Skills 或配置好的集成连接；读写文件时，确认 Project 工作目录。
4. 保存并启用 Agent，在 Chat 中用一小段材料验证回复，检查工具调用与实际结果。

## 从单个 Agent 到固定流程

单个 Agent 可以在职责范围内判断如何完成任务。当任务要求每次都经过明确的步骤，例如“整理后必须审查，审查后必须人工确认”，可以用 [Agentflow]({{< relref "/docs/features/agentflow" >}}) 编排这些步骤。

Agent 的操作范围取决于实际配置的工具与权限。指令本身不会授予文件或外部服务的访问权；模型的判断和执行结果也需要验证。

[创建自定义 Agent]({{< relref "/docs/guides/agents" >}}) · [配置 Tools 与 Skills]({{< relref "/docs/guides/tools-skills" >}})
