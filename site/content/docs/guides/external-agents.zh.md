---
title: "接入外部 Agent"
description: "使用 Claude Code、Codex 或 Pi 执行任务，为不同用途分别配置 Agent。"
weight: 30
lastmod: 2026-09-15
translationKey: docs/guides/external-agents
---

接入外部 Agent 后，任务由 Claude Code、Codex 或 Pi 等外部工具实际执行，AGW 提供统一的配置、对话和工作流入口。你可以继续使用熟悉的外部工具，同时在 AGW 中管理它们的使用方式。

前提：对应 CLI 已安装在实际执行节点，并能在 Server 使用的账号与环境下工作。浏览器里安装 CLI 无效；容器执行需要容器内可用。

## 同一种外部 Agent，多份独立配置

同一种外部 Agent 可以在 AGW 中创建多份 Agent 定义，每份分别选择模型和配置。例如，都使用 Claude Code 执行任务，但根据用途建立两个 Agent：

| AGW 中的 Agent | 实际运行的外部工具 | 使用的模型 | 用途 |
| --- | --- | --- | --- |
| Coding | Claude Code | model1 | 编写和修改代码 |
| Review | Claude Code | model2 | 审查代码并提出改进建议 |

创建这两个 Agent 时，都选择 **External → Claude Code**，再分别选择指向 `model1` 和 `model2` 的 Model Provider。这里的模型名称仅为示例，需要替换为服务商实际提供、且兼容 Anthropic 协议的模型。

这样，在 Chat 中选择 Coding 或 Review，就会使用各自配置的模型；也可以在 Agentflow 的不同节点中使用它们。无需为了切换用途反复修改同一份 Agent 配置。

这里的隔离是指 AGW 中各份 Agent 定义的配置独立，不会自动创建独立的操作系统账号或文件环境。如果未选择 Model Provider，该定义仍会使用外部工具自身的模型配置。

## 配置步骤

1. 先在执行节点验证 CLI 可用，并完成它所需的认证或模型配置。
2. 在 Agents 配置对应的外部 Agent 类型。
3. 选择 Project，并确认主工作目录对执行进程可见。
4. 按需选择兼容的 Model Provider；留空时使用外部工具自身配置。
5. 发送一个简短任务，核对工作目录、输出和权限模式。

| 外部 Agent | 可选 Model Provider | 权限说明 |
| --- | --- | --- |
| Claude Code | Anthropic | 使用该目标声明的权限能力 |
| Codex | OpenAI Responses | 当前仅支持 FullAccess |
| Pi | 三种提供商协议均可 | 当前仅支持 FullAccess |

![AGW Desktop：External Agent 可选择 Claude Code、OpenAI Codex 或 Pi；对应 CLI 需在执行主机另行安装和配置。](/images/screenshots/external-agent-types.png)
{caption="AGW Desktop：External Agent 可选择 Claude Code、OpenAI Codex 或 Pi；对应 CLI 需在执行主机另行安装和配置。"}

## 配置生效与限制

Chat 只展示目标支持的权限模式；服务端也会验证能力。修改权限或 Agent 配置影响下一回合，当前回合和持久化恢复保留原始快照。

Configured integrations 的 Connection 能力当前不会注入外部 Codex 或 Claude Agent。外部工具自身支持什么能力，需要在其环境中配置和验证。

CLI 不可用时检查可执行文件、运行账号、环境变量及服务日志。例如，终端中可运行而 AGW 中无法启动时，先检查 Server 账号的程序搜索路径（PATH）是否包含该 CLI。

## 实现与参考

- [External agent runtime](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
- [Integration limits](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/README.md)
