---
title: "创建自定义 Agent"
description: "用模型、指令与必要能力定义 Agent。"
weight: 20
lastmod: 2026-09-15
translationKey: docs/guides/agents
---

Agent 是一份可重复使用的助手配置：模型决定它如何理解问题，指令说明它要做什么，工具决定它能执行哪些操作。例如，可以分别创建“代码解释助手”和“文档审查助手”，在 Chat 中按任务选择。

开始前，先按[模型提供商指南]({{< relref "/docs/guides/providers" >}})准备可用的 Model Provider。本页介绍由 AGW 运行的自定义 Agent；Claude Code、Codex 和 Pi 请参阅[外部 Agent]({{< relref "/docs/guides/external-agents" >}})。

## 创建步骤

1. 打开 Agents，创建自定义 Agent，填写能表达职责的名称。
2. 选择 Model Provider，在指令中写明任务、输入以及预期输出。
3. 按需选择工具、Skills 和配置好的集成连接；文件相关能力需要正确的 Project 工作目录。
4. 保存并确认 Agent 已启用，在 Chat 选择它运行一个小任务。

例如，先创建只回答问题的“代码解释助手”，确认模型可用后再加入只读文件能力。提示词不能授予超出实际运行权限的工具访问。

![AGW Desktop：创建自定义 Agent 的示例表单。保存前还需选择 Model Provider。](/images/screenshots/agent-create.png)
{caption="AGW Desktop：创建自定义 Agent 的示例表单。保存前还需选择 Model Provider。"}

## 写清 Agent 的职责

指令最好包含任务范围和输出要求。例如，文档审查助手可以使用：

```text
审查我提供的文档，找出难懂、重复或缺少解释的地方。
按“原文、问题、建议改写”列出结果。保留原文中的事实与限制。
无法确认的内容请标出来，不要补写未经证实的功能。
```

先粘贴一小段文字验证输出。需要它直接读取项目文档时，再添加文件读取工具，并在对应 Project 中运行。能力是否可用取决于实际工具配置和权限，写在指令里的要求本身不会授予访问权。

## 修改与复用

Agent 定义修改在下一回合生效，同时保留现有会话身份。活跃回合使用开始时的配置快照，不会在执行中途切换权限或目录。

管理界面可以复制 System Agent；External Agent 不支持这种复制。复制后应检查模型、能力与项目环境再运行。

## 验证

用一条与职责相符的问题验证输出，并检查工具活动是否只包含预期能力。若没有工具，检查绑定、工具目录以及 Connection 的 Ready 状态；不要用提高权限代替修复缺失配置。

## 实现与参考

- [Agents module](https://github.com/zxyao145/agw/tree/main/src/server/Agw.Agents)
- [Runtime lifecycle](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
