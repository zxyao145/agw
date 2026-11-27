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

## 使用 ACP 外部 Agent

ACP（Agent Client Protocol）是基于 stdio JSON-RPC 的外部 Agent 协议。AGW 支持任何兼容 ACP 协议的外部程序（例如 Gemini CLI、OpenCode、Goose、Codex ACP 等），可通过官方 ACP Registry 安装或使用主机上已有的自定义命令启动。

### 方式一：从 ACP Registry 选择

1. 先在执行主机（`Standalone` 或 `Data Plane`）安装目标 Agent，安装命令与运行 Server 使用相同的操作系统账号和 `AgwDataDir`（或 `AcpAgents:InstallRoot`）：

```bash
./agw-server acp install goose codex-acp
```

容器部署需在派生执行镜像构建阶段完成安装，详见[Server 扩展与 ACP 主机配置]({{< relref "/docs/operations/extensions" >}})。Server 在运行阶段不会自动联网下载 Agent 程序。

2. 在 **Agents → Create** 中选择 **Agent Type** 为 `External`，外部类型选择 `ACP`。
3. 在表单的 **ACP Registry** 下拉选择器中搜索并选中已安装的 Agent（可点击 **Refresh** 刷新目录）。选中后表单会自动更新 **Extra Settings** 的 `registry` 字段并清除 `command` / `args`。若当前 **Extra Settings** JSON 存在语法错误，需先修正后再使用选择器。

### 方式二：使用自定义命令

如果主机上已经直接安装了支持 ACP 的 CLI，可跳过 Registry，在 **Extra Settings** 中填写 `command` 与可选的 `args`：

```json
{
  "command": "gemini",
  "args": ["--acp"],
  "configOptions": {
    "model": "gemini-2.5-pro"
  }
}
```

### ACP Extra Settings 字段说明

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `registry` | object，可选 | `{ "agentId": "<registry id>", "version": "<major.minor.patch>" }`。`version` 可省略，省略时自动选用安装目录中已安装的最高版本 |
| `command` | string，可选 | 自定义启动命令的可执行文件名称或路径 |
| `args` | string[]，可选 | 自定义命令的参数列表，只能与 `command` 同时使用 |
| `authMethodId` | string，可选 | 初始化连接后调用 `authenticate` 时使用的认证方式 ID |
| `modeId` | string，可选 | 建立会话后切换到的目标模式 ID |
| `configOptions` | object，可选 | 建立会话后设置的配置项键值表，键为配置项 ID，值为字符串或布尔值（例如设置模型） |

`registry` 与 `command` 必须二选一，不能同时出现在同一份 **Extra Settings** 中。使用 Registry 并附加配置项的示例如下：

```json
{
  "registry": { "agentId": "goose" },
  "configOptions": {
    "model": "claude-sonnet-4-5"
  }
}
```

### 会话恢复、凭据与多副本运行

- **会话延续**：同一对话的多个回合会保存并复用 ACP `sessionId`。当进程重启、Runtime 重建或在 Distributed 下切换到其他 Data Plane 副本时，AGW 会通过 `session/resume` 或 `session/load` 恢复会话。
- **凭据与环境变量**：ACP 进程以 Server 操作系统账号运行。可通过 Agent 或 Project 的环境变量提供 API Key 或配置目录（例如 `CODEX_HOME`），或在运行 Server 的账号下提前完成对应 CLI 的登录。
- **权限与 Distributed 模式**：InProcess 模式下，ACP 支持 `AlwaysAsk`、`AllowSameArguments` 与 `FullAccess` 三种权限模式，权限请求会按当前模式自动放行或提交给用户审批。Distributed 模式下仅支持 `FullAccess`，且所有 Data Plane 副本必须部署相同的 Agent 版本并挂载一致的共享会话目录。

## 配置生效与限制

Chat 的权限下拉框始终列出三种模式，目标不支持的模式显示为不可选并说明原因；服务端也会验证能力。ACP 在 InProcess 下支持三种模式，在 Distributed 下仅支持 FullAccess。修改权限或 Agent 配置影响下一回合，当前回合保留开始时的配置快照。

Claude Code、Codex 和 Pi 在 Chat 中直接运行时需要 InProcess。ACP 和远程 A2A Agent 支持 InProcess 与 Distributed；Distributed 下的 ACP 需要使用 FullAccess，并让各副本共用 ACP Agent 的会话目录。详见[Control/Data Plane 部署]({{< relref "/docs/operations/split" >}})和[ACP 部署配置](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md)。

AGW 中的 Instructions、Tools、Skills、MCP Tool Server 和 Integrations 配置都不会交给任何 External Agent（包括 Pi、ACP 和 A2A），表单中的这些标签不可编辑；External Agent 只会读到已有的 User Memory 作为上下文。外部工具自身支持什么能力，需要在其环境中配置和验证。

CLI 不可用时检查可执行文件、运行账号、环境变量及服务日志。例如，终端中可运行而 AGW 中无法启动时，先检查 Server 账号的程序搜索路径（PATH）是否包含该 CLI。

## 实现与参考

- [External agent runtime](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
- [Integration limits](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/README.md)
