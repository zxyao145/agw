# Agw.Tools

`Agw.Tools` 负责内置 Tool 的编目，以及 Tool 与 ToolBlock 的运行时物化。

## 能力模型

```text
Tool Capability
├── Tool       可独立选择
└── ToolBlock  一组必须原子化管理的成员 Tool
```

Tool 对模型暴露一个可调用操作，可以单独添加或删除。ToolBlock 表示行为和状态必须保持一致的一组 Tool，因此其成员只能整体选择、物化和删除。

当前 ToolBlock：

- `todo`：`todos_add`、`todos_list`、`todos_complete` 等 Todo 工具。
- `mode`：`mode_get`、`mode_set`。
- `project-memory`：Agw Project Memory 工具，存储可选数据库或
  `<Project.Workspace>/.agw/memory`。同一项目的 Agent 与会话共享记忆；
  Filesystem 模式下，指向同一 Workspace 的多个项目也共享记忆。
- `file-access`：限定在 `Project.Workspace` 下的 Harness 文件工具。
- `background-agents`：只允许一层的后台 Agent 委派工具。

需要上下文物化的独立 Tool：

- `web_search`：Provider 支持时使用 Hosted marker，否则物化 Agw Local Search，并产生 `tool-warning`。
- `run_shell`：物化全局配置的 Local 或 Docker Shell Executor 及其 Context Provider，仍可单独选择。

原有 `bash`、`powershell` 保持为普通独立 Tool。

## 数据与选择语义

Agent 和 Project 统一使用一个强类型 `tools` 字段：

```json
[
  {
    "kind": "tool",
    "definition": {
      "name": "web_search",
      "options": {}
    }
  },
  {
    "kind": "toolBlock",
    "definition": {
      "name": "project-memory",
      "options": {
        "storage": "database"
      }
    }
  }
]
```

- 外层 `ToolValueObject` 以 `kind` 多态，取值为 `tool` 或 `toolBlock`。
- 内层 `definition` 以 `name` 多态，反序列化为具体
  `ToolDefinition` 或 `ToolBlockDefinition`。
- `options` 必须存在且必须为 JSON object；无配置项时写为 `{}`。
- Agent 与 Project 按 `definition.name` 合并；同名时 Project
  Definition 覆盖 Agent Definition。
- `null` 与 `[]` 都表示当前层不增加配置。
- `background-agents` 仅允许 Agent 配置。
- 新建 Agent 和 Project 的 `tools` 默认为 `[]`。
- 数据库中的旧值（如 `["web_search"]`）可兼容读取，并在后续保存时写回新格式；未知旧名称会明确失败，不会静默忽略。

ToolBlock 成员不会作为独立 catalog item 出现。若将 `todos_add` 写入
独立 Tool 配置，系统会明确提示应选择其所属的 `todo` ToolBlock。

## Catalog

`GET /api/tools` 是唯一的 Tool catalog 接口，同时返回两种 item：

```json
{
  "kind": "toolBlock",
  "name": "todo",
  "displayName": "Todo",
  "memberToolNames": ["todos_add", "todos_list", "todos_complete"],
  "scopes": 3,
  "requiresWorkspace": false
}
```

`kind` 为 `tool` 或 `toolBlock`。`/api/tools/by-category` 和
`/api/tools/{name}` 使用相同结构，不再存在单独的 ToolBlock endpoint。

## 运行时架构

```text
Agent + Project definitions
          |
          v
AgentCapabilityComposer
  |-- ToolRegistryService -------- 普通 Tool、上下文 Tool
  |-- ToolValueResolution -------- 按 definition.name 合并 Agent/Project
  |-- ToolBlockRegistry ---------- 原子 ToolBlock
  |-- Connections / MCP / Skills
          |
          v
ToolContribution
  |-- Tools
  |-- ContextProviders
  |-- LoopEvaluators
  |-- AutoApprovalRules
  |-- Warnings
  `-- 持有的 IAsyncDisposable 资源
          |
          v
AsAgwAgent
```

`ToolContribution` 是上下文 Tool 与 ToolBlock 共用的物化结果。Composer 将其中的 Tools 展平给模型调用，并把 Provider、Evaluator、审批规则和 warning 交给 Agent pipeline。

聚合 Contribution 通过持有子 Contribution 形成资源所有权树。Agent capability lease 释放时，资源按后进先出顺序清理；物化中途失败时，已经创建的资源也会被释放。

Catalog 构建和运行时组合都会校验名称：

- 独立 Tool 不能重名；
- ToolBlock 不能重名；
- 一个成员 Tool 不能属于多个 ToolBlock；
- ToolBlock 名称不能与 Tool 或成员名称冲突；
- Connection、MCP、上下文 Tool 和 ToolBlock 最终贡献的 Tool 不能重名。

## 关键类型

- `IAgwTool`：普通内置 Tool。
- `IContextualTool`：创建时需要 Agent、Project、workspace、Provider 或环境变量上下文的独立 Tool。
- `IToolBlock`：原子 Tool 集合。
- `ToolValueObject`：外层以 `kind` 区分的持久化值。
- `ToolDefinition`：以 `name` 区分的独立 Tool 强类型配置。
- `ToolBlockDefinition`：以 `name` 区分的 ToolBlock 强类型配置。
- `ToolBlockDescriptor`：catalog 元数据及成员名称。
- `ToolValueResolution`：Agent/Project 合并规则。
- `ToolMaterializationContext`：物化所需的运行时上下文。
- `ToolContribution`：物化后的行为与资源所有权。
- `ToolRegistryService`：统一 catalog 与独立 Tool 物化入口。
- `ToolBlockRegistry`：ToolBlock 校验和物化入口。

`AgwWorkspaceProvider` 不属于 catalog。它位于 `Agw.Agents`，作为所有 System Agent 都会添加的核心 Context Provider。

## 状态与审批

状态由需要它的运行时行为拥有，而不是由 catalog 拥有：

- Todo、Mode 使用 MAF session provider。
- Project Memory 使用 Agw 自有且无 Agent Session 状态的 Provider。
- Project Memory 数据库存储按 Project ID 隔离。
- Project Memory 文件系统存储位于 `<Project.Workspace>/.agw/memory`；指向同一
  Workspace 的多个 Project 共享该目录。
- Background relation 与结果由对应 runtime 持久化。

Tool Approval 属于 Agent pipeline。Tool 或 ToolBlock 只贡献必要的自动审批规则，`AsAgwAgent` 统一应用审批中间件；文件写入和 Shell 执行仍要求审批。

运行时消息 author 为 `tools`，消息类型为：

- `tool-todo-snapshot`
- `tool-mode-status`
- `tool-background-task-status`
- `tool-warning`

## 扩展方式

### 新增普通 Tool

不需要 Agent/Project 上下文时，使用 `IAgwTool` 或现有 Tool attribute。同时增加具体 `ToolDefinition`、稳定的 `JsonDerivedType` 名称映射和执行实现；启动校验会保证 Definition 与执行实现一一对应。

### 新增上下文 Tool

1. 实现 `IContextualTool`。
2. 增加具体 `ToolDefinition` 及其 `JsonDerivedType` 映射。
3. 提供 `kind = tool` 的 `ToolInfo` descriptor。
4. 返回 `ToolContribution`。
5. 将 Executor、Provider、Client 等生命周期全部移交给 Contribution。
6. 在 `AddTools()` 注册。

### 新增 ToolBlock

1. 在 `Agw.Data` 增加 `ToolBlockDefinition` 派生类型和稳定的 `name` discriminator。
2. 在 `ToolBlockNames` 增加对应运行时名称。
3. 在 `ToolBlocks/Blocks` 下创建独立文件夹并实现 `IToolBlock`。
4. 在 `ToolBlockDescriptor.MemberToolNames` 中列出全部成员。
5. 在一个 `ToolContribution` 中物化所有成员及其状态 Provider。
6. 在 `AddTools()` 注册。
7. 增加 Registry、Resolution、物化、API 和 UI 测试。

不要将 ToolBlock 成员注册为独立 Tool，也不要增加隐式默认选择。

## 验证

```bash
dotnet test tests/Agw.Tools.Tests/Agw.Tools.Tests.csproj
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj
dotnet build Agw.slnx

cd src/clients
pnpm exec turbo run build --filter=@agw/tools --filter=@agw/agents --filter=@agw/projects
pnpm test:boundaries
```
