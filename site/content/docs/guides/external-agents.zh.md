---
title: "接入外部 Agent"
description: "使用 Claude Code、Codex、Pi、ACP 或远程 A2A Agent 执行任务，为不同用途分别配置 Agent。"
weight: 30
lastmod: 2026-11-26
translationKey: docs/guides/external-agents
---

接入外部 Agent 后，任务由 Claude Code、Codex、Pi、ACP Agent 或远程 A2A Agent 等外部服务或工具实际执行，AGW 提供统一的配置、对话和工作流入口。你可以继续使用熟悉的外部工具或远端 Agent，同时在 AGW 中管理它们的使用方式。

前提：使用本地 CLI 类外部 Agent 时，对应 CLI 已安装在实际执行节点，并能在 Server 使用的账号与环境下工作；浏览器里安装 CLI 无效，容器执行需要容器内可用。使用远程 A2A Agent 时，执行主机需要能访问目标 Agent Card URL 及接口地址。

ACP Agent 可以从 registry 安装，也可以通过 Server 可用的 command 启动。请在 **Extra Settings** 中选择已安装的 Agent，或配置 `command` 与可选的 `args`。安装说明见[ACP 部署文档](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md)。

## 同一种外部 Agent，多份独立配置

同一种外部 Agent 可以在 AGW 中创建多份 Agent 定义，每份分别选择模型和配置。例如，都使用 Claude Code 执行任务，但根据用途建立两个 Agent：

| AGW 中的 Agent | 实际运行的外部工具 | 使用的模型 | 用途 |
| --- | --- | --- | --- |
| Coding | Claude Code | model1 | 编写和修改代码 |
| Review | Claude Code | model2 | 审查代码并提出改进建议 |

创建这两个 Agent 时，都选择 **External → Claude Code**，再分别选择指向 `model1` 和 `model2` 的 Model Provider。这里的模型名称仅为示例，需要替换为服务商实际提供、且兼容 Anthropic 协议的模型。

这样，在 Chat 中选择 Coding 或 Review，就会使用各自配置的模型；也可以在 Agentflow 的不同节点中使用它们。无需为了切换用途反复修改同一份 Agent 配置。

这里的隔离是指 AGW 中各份 Agent 定义的配置独立，不会自动创建独立的操作系统账号或文件环境。如果未选择 Model Provider，模型来自该 Agent 的 **Extra Settings** 或外部工具自身的模型配置。

## 配置步骤

1. 先在执行节点验证 CLI 可用（或确认远程 A2A Agent Card URL 可达），并完成所需的认证或模型配置。
2. 在 **Agents** 中点击 **Create**，**Agent Type** 选择 `External`，再选择外部 Agent 类型。类型创建后不能修改。
3. 选择 Project，并确认主工作目录对执行进程可见。
4. 按所选外部 Agent 类型填写配置：
   - **Claude Code、Codex 或 Pi**：按需选择兼容的 Model Provider；留空时使用 **Extra Settings** 或外部工具自身配置。
   - **ACP**：在 **Extra Settings** 中使用 `configOptions`、`authMethodId` 和 `modeId` 配置模型、认证和运行模式。
   - **A2A**：填写 **Agent Card URL**（例如 `https://example.com/.well-known/agent-card.json`），按需添加绑定到 origin 的 HTTP 请求头凭据，然后点击 **Fetch card**（编辑时可点击 **Refresh card**）解析远端接口。
5. 发送一个简短任务，核对工作目录、输出和权限模式。

Claude Code 和 Codex 通过各自 SDK 的目录选项获得 Project 的附加目录，Pi 通过每一回合的上下文获得目录清单；三者的默认工作目录都是主目录。

| 外部 Agent | 模型与端点配置 | 权限说明 |
| --- | --- | --- |
| Claude Code | 可选 Anthropic Model Provider | 使用该目标声明的权限能力 |
| Codex | 可选 OpenAI Responses Model Provider | 当前仅支持 FullAccess |
| Pi | 可选 Model Provider（OpenAI / Anthropic 协议） | 当前仅支持 FullAccess |
| ACP | 通过 ACP Agent 设置模型与认证 | InProcess 支持三种模式；Distributed 仅支持 FullAccess |
| A2A | 远程 Agent Card URL 与按 origin 绑定的请求头凭据 | 仅支持 FullAccess（工具在远端运行） |

使用 registry 中的 ACP Agent 时，需要在每个执行主机安装选定版本。Distributed 模式下，各 Data Plane 副本还需共用 ACP Agent 的会话目录。

## 配置生效与限制

Chat 的权限下拉框始终列出三种模式，目标不支持的模式显示为不可选并说明原因；服务端也会验证能力。ACP 在 InProcess 下支持三种模式，在 Distributed 下仅支持 FullAccess。修改权限或 Agent 配置影响下一回合，当前回合保留开始时的配置快照。

Claude Code、Codex 和 Pi 在 Chat 中直接运行时需要 InProcess。ACP 和远程 A2A Agent 支持 InProcess 与 Distributed；Distributed 下的 ACP 需要使用 FullAccess，并让各副本共用 ACP Agent 的会话目录。详见[Control/Data Plane 部署]({{< relref "/docs/operations/split" >}})和[ACP 部署配置](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md)。

AGW 中的 Instructions、Tools、Skills、MCP Tool Server 和 Integrations 配置都不会交给任何 External Agent（包括 Pi、ACP 和 A2A），表单中的这些标签不可编辑；External Agent 只会读到已有的 User Memory 作为上下文。外部工具自身支持什么能力，需要在其环境中配置和验证。

CLI 不可用时检查可执行文件、运行账号、环境变量及服务日志。例如，终端中可运行而 AGW 中无法启动时，先检查 Server 账号的程序搜索路径（PATH）是否包含该 CLI。

## 实现与参考

- [External agent runtime](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
- [Integration limits](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/README.md)
