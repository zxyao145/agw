# Execution 执行子系统

`Execution` 负责把客户端命令转换为 Agent 或 Agentflow 的一次次执行 turn，并管理连接状态、运行时复用、中断、HumanGate、用户信息交互、消息输出和资源释放。它既包含 SignalR 实时执行入口，也提供给 A2A、Jobs 等模块复用的 Agent/Agentflow 执行服务。

Connection 生命周期、Command Handler 扩展方式与状态所有权的决策依据见 [`ADR 0001`](../../../../docs/adr/0001-execution-connection-command-architecture.md)。

这里刻意区分了五种生命周期：

| 对象 | 生命周期 | 主要职责 |
| --- | --- | --- |
| `ExecutionHub` | 一次 SignalR Hub 调用 | 接收命令并把 `AgwException` 转为 `HubException` |
| `ExecutionConnection` | 一条 SignalR 实时连接 | 串行分派命令、管理 attached 状态和 connection 级 DI scope |
| `ExecutionConnectionContext` | 同一条 SignalR 连接 | 维护 settings、task、workspace、target、runtime 的一致性 |
| `RuntimeBase` | 同一执行目标的多轮对话 | 持有当前 `ActiveTurn`，负责中断、等待空闲和释放 |
| `ActiveTurn` | 一次 `ExecCommand` | 跟踪执行任务、取消源和 HumanGate 响应入口 |

`Microsoft.Agents.AI.AgentSession` 只存在于 `AgentRuntime` 内部，不承担 SignalR connection 或 turn 的职责。

## 目录结构

```text
Execution/
├── Agentflows/
│   ├── AgentflowRuntimeService.cs
│   ├── AgentflowWorkflowCompiler.cs
│   ├── HumanGateApproval.cs
│   └── IAgentflowRuntimeService.cs
├── Agents/
│   ├── Dtos/
│   ├── Middleware/
│   ├── Utils/
│   ├── AgentRuntimeService.*.cs
│   ├── AgentSessionStateStore.cs
│   └── IAgentRuntimeService.cs
├── Commands/
│   ├── Abstracts/
│   │   ├── AgentRunCommand.cs
│   │   └── IExecutionCommandHandler.cs
│   ├── Exec/
│   │   ├── ExecCommand.cs
│   │   └── ExecCommandHandler.cs
│   ├── Hip/
│   │   ├── HumanResponseCommand.cs
│   │   └── HumanResponseCommandHandler.cs
│   ├── Interrupt/
│   │   ├── InterruptCommand.cs
│   │   └── InterruptCommandHandler.cs
│   ├── Mode/
│   │   ├── SetModeCommand.cs
│   │   └── SetModeCommandHandler.cs
│   ├── Permission/
│   │   ├── SetPermissionModeCommand.cs
│   │   └── SetPermissionModeCommandHandler.cs
│   ├── Setting/
│   │   ├── SettingCommand.cs
│   │   ├── PermissionMode.cs
│   │   └── SettingCommandHandler.cs
│   ├── ExecutionCommandDispatcher.cs
│   └── ExecutionCommandRegistration.cs
├── Messaging/
│   └── IExecutionMessageSink.cs
├── Runtimes/
│   ├── RuntimeBase.cs
│   ├── RuntimeFactory.cs
│   ├── AgentRuntime.cs
│   └── AgentflowRuntime.cs
├── Connections/
│   ├── ExecutionConnection.cs
│   ├── ExecutionConnectionContext.cs
│   ├── ExecutionConnectionContextFactory.cs
│   ├── ExecutionSettings.cs
│   └── ExecutionTarget.cs
├── Summaries/
│   ├── AgentTurnSummaryService.cs
│   ├── IAgentTurnSummaryService.cs
│   ├── ISummaryChatClientFactory.cs
│   └── SummaryChatClientFactory.cs
├── Transport/SignalR/
│   ├── ExecutionHub.cs
│   ├── ExecutionConnectionRegistry.cs
│   ├── IExecutionHubClient.cs
│   └── SignalRExecutionMessageSink.cs
├── Turns/
│   ├── ActiveTurn.cs
│   ├── RuntimeTurnContext.cs
│   ├── RuntimeTurnContextAccessor.cs
│   ├── TurnPipeline.cs
│   ├── TurnMessageFactory.cs
│   └── HumanGateApprovalCoordinator.cs
└── AgwMessageUtil.cs
```

### `Commands`

这里按 command 垂直切片：每个子目录共置 transport contract 与对应 handler，修改一种 command 时不需要跨 `Contracts/` 和 `Commands/` 两棵目录跳转。`Abstracts/` 只保存所有切片共享的 `AgentRunCommand` 和 handler 接口；dispatcher 与注册 seam 留在 `Commands/` 根目录。

`AgentRunCommand` 使用 `type` 作为 JSON discriminator，目前包含六种命令。派生类型映射不写在 contract 基类上，而由 command 的 DI 注册统一提供：

| Command | 作用 | 是否改变 connection 状态 |
| --- | --- | --- |
| `SettingCommand` | 设置 project、context、环境变量和默认权限策略 | 是；settings 变化时清理旧 runtime、task 和 target |
| `ExecCommand` | 指定 Agent/Agentflow 目标和用户输入，启动一个 turn | 是；解析 task，并创建或复用 runtime |
| `InterruptCommand` | 请求中断当前 turn | 否；只转发给当前 `ActiveTurn` |
| `SetModeCommand` | 切换支持 mode 的 Agent | 是；空闲时立即应用，活动 turn 结束后应用最后一次请求 |
| `SetPermissionModeCommand` | 切换工具审批策略 | 是；立即更新 settings 和当前活动 turn，不重建 runtime |
| `HumanResponseCommand` | 提交审批或用户信息交互响应 | 否；只转发给当前 turn 的协调器 |

`SettingCommand.Resume` 是服务端属性，带有 `[JsonIgnore]`。transport command 自身的等价性不包含 `Resume`；复制出的 `ExecutionSettings` 会包含它，因为 resume 变化需要使 connection-owned runtime 失效。

`ExecutionCommandDispatcher` 在构造时把内部 handler adapter 按 command CLR type 建成字典。重复注册同一命令会立即抛出 `AgwException`；运行时收到未注册命令也会失败，而不是落入默认分支。

每种命令实现独立的 `IExecutionCommandHandler<TCommand>`。handler 只把输入翻译为 `ExecutionConnectionContext` 的稳定操作，不读取 runtime、不修改状态字段，也不依赖 SignalR。新增 command 时不需要修改 dispatcher 或 `AgentRunCommand`。

`AddExecutionCommand<TCommand, THandler>(discriminator)` 是唯一注册 seam，同时注册 typed handler、dispatcher adapter 和 SignalR JSON discriminator。新增 compile-time command 只需定义 contract、实现 handler，并调用一次该扩展方法。

### `Connections`

`ExecutionConnection` 是命令并发与资源生命周期的边界。所有 command 先经过 `_commandGate`，因此同一条 connection 不会并发执行状态变更；它本身只持有 dispatcher、context、DI scope 和 attached 状态。

`ExecutionConnectionContext` 是状态内核，独占：

- 当前不可变 `ExecutionSettings`；
- 已解析的 `TaskProjection`；
- 已解析并规范化的 workspace；
- 当前 `ExecutionTarget`；
- 可跨 turn 复用的 `RuntimeBase`；
- user、消息 sink、host cancellation token 和 waiting-for-human 状态。

它通过 `ApplySettingsAsync`、`StartTurnAsync`、`InterruptTurnAsync` 和 `SubmitHumanDecisionAsync` 提供原子操作，并以只读属性共享 project/context/workspace/agent/task/user 数据；它不公开 `RuntimeBase`、`ActiveTurn` 或状态 setter。`ExecCommand` 启动后台 turn 后会很快返回，command gate 随即释放，后续 interrupt 和 HumanGate response 才能进入。

`Messaging` 定义 transport-neutral 的 `IExecutionMessageSink`。Connection 和 runtime 只面向该接口输出消息，SignalR adapter 提供具体实现。

### `Runtimes`

`RuntimeBase` 维护“同一 runtime 同时最多一个活动 turn”的约束。它负责注册 `ActiveTurn`、等待执行结束、清除活动引用、中断转发和异步释放。

`AgentRuntime` 持有实际 `AIAgent`、SDK `AgentSession`、session key 和独立取消源。`AgentRuntimeService` 负责从持久化定义构造 Agent、加载技能和工具、创建外部 Agent，并在执行结束后保存 session state。

`AgentflowRuntime` 保存 Agentflow id、task、settings 和 `AgentflowRuntimeService`。每个 Agentflow turn 都会创建新的 `HumanGateApprovalCoordinator`，workflow 本身由 `AgentflowWorkflowCompiler` 生成。

`RuntimeFactory` 负责把已解析好的 execution/turn 输入对应到具体 runtime，并将 runtime 输出接入统一的 `TurnPipeline`。task 与 workspace 的解析由 `ExecutionConnectionContext` 统一完成。Agent runtime 只有在 project 和 context 仍兼容时才会复用；settings 或 target 变化会先释放旧 runtime。

### `Turns`

turn 是一次用户输入到执行结束的完整过程。`RuntimeTurnContext` 是不可变快照，包含 settings、task、target、project/context/agent 标识、当前用户、绝对 workspace、消息 sink 和 HumanGate 状态回调。`RuntimeTurnContextAccessor` 使用 `AsyncLocal` 在执行任务内部暴露该快照，作用域在 turn 结束后恢复；connection 级可变状态不会进入 `AsyncLocal`。

`IRuntimeTurnContextAccessor` 只公开 `Current`。`Push` 仅在 Agents 模块内部由 `RuntimeBase` 使用，Jobs 等 runtime skill 只能读取当前 turn，不能伪造或覆盖执行上下文。

需要用户提供信息的 Tool 使用 `HumanInteractionRequiredAIFunction` 包装，并由各自的 `IHumanInteractionProtocol` 负责生成请求、校验响应和绑定 Tool 参数。`RuntimeFactory` 仅在交互式 turn 中通过 `HumanInteractionContextAccessor` 提供 channel；Jobs、后台 Agent 等无人值守执行没有 channel，遇到此类 Tool 会明确失败而不会无限等待。此通道不会生成 `ToolApprovalRequestContent`，因此 Tool 的全局授权和自动审批规则不会跳过用户信息输入。

`TurnPipeline` 统一输出协议：

1. 先发送 `turn-start`；
2. 转发 runtime 消息；
3. 正常结束发送 `turn-finished(status=completed)`；
4. 收到取消时发送 `turn-finished(status=interrupted)`；
5. runtime 抛错时先发送 `AgwErrorContent`，再发送 `turn-finished(status=failed)`。

当 `stream=false` 时，普通消息会缓冲到 runtime 执行结束后再发送。`human-gate-*` 控制消息不缓冲，否则客户端无法及时提交审批结果。runtime 自己产生的 `turn-finished` 会被过滤，避免重复终止消息。

### `Transport/SignalR`

SignalR Hub 路由为 `/api/hubs/exec`，公开一个服务端方法：

```text
DispatchCommand(AgentRunCommand)
```

服务端通过 typed client callback 返回消息：

```text
ReceiveMessage(AgwMessage)
```

`ExecutionConnectionRegistry` 是 singleton，只负责把 SignalR `connectionId` 映射到 `ExecutionConnection`。每条 connection 拥有独立的异步 DI scope；`SignalRExecutionMessageSink` 通过 `IHubContext` 向指定客户端发送消息，不捕获短生命周期的 Hub 实例。

## 总体架构

```mermaid
flowchart TB
    Client["Client"] -->|"SignalR command"| Hub["ExecutionHub"]
    Hub --> Registry["ExecutionConnectionRegistry"]
    Registry --> Connection["ExecutionConnection"]
    Connection --> Dispatcher["ExecutionCommandDispatcher"]

    Dispatcher --> Setting["SettingCommandHandler"]
    Dispatcher --> Exec["ExecCommandHandler"]
    Dispatcher --> Interrupt["InterruptCommandHandler"]
    Dispatcher --> Mode["SetModeCommandHandler"]
    Dispatcher --> Permission["SetPermissionModeCommandHandler"]
    Dispatcher --> Human["HumanResponseCommandHandler"]

    Setting --> Context["ExecutionConnectionContext"]
    Exec --> Context
    Interrupt --> Context
    Mode --> Context
    Permission --> Context
    Human --> Context
    Context --> ProjectService["IProjectAppService"]
    Context --> TaskService["ITaskAppService"]
    Context --> Factory["RuntimeFactory"]
    Factory --> AgentRuntime["AgentRuntime"]
    Factory --> AgentflowRuntime["AgentflowRuntime"]
    AgentRuntime --> RuntimeBase["RuntimeBase"]
    AgentflowRuntime --> RuntimeBase
    RuntimeBase --> TurnPipeline["TurnPipeline"]
    TurnPipeline --> Sink["IExecutionMessageSink"]
    Sink --> SignalRSink["SignalRExecutionMessageSink"]
    SignalRSink -->|"ReceiveMessage"| Client
```

架构依赖从 transport 指向执行内核：SignalR adapter 只认识 connection 和 command；typed handler 只认识自己的 command 与 `ExecutionConnectionContext`；runtime 不依赖 Hub，只依赖 transport-neutral 的 `IExecutionMessageSink`。

## 数据处理流程

### 建立连接

1. `ExecutionHub.OnConnectedAsync` 调用 registry。
2. registry 为 connection 创建独立 `AsyncServiceScope`。
3. 从该 scope 解析 `ExecutionCommandDispatcher`、handlers 和 `ExecutionConnectionContextFactory`。
4. 创建 `SignalRExecutionMessageSink` 与 `ExecutionConnection`，然后按 connection id 保存。

### 应用 Settings

`SettingCommandHandler` 只把 transport contract 转换为不可变 `ExecutionSettings`，然后调用 `ExecutionConnectionContext.ApplySettingsAsync`。Context 在活动 turn 期间返回 busy error；空闲且内容变化时释放旧 runtime，并清空 resolved task、workspace 和 target。

权限策略的初始值仍由 `SettingCommand.permissionMode` 保存。运行中切换通过 `SetPermissionModeCommand` 完成，因此不会触发 settings 的 busy 检查或重建 runtime；新的策略会立即应用到当前 turn。切换到 `FullAccess` 时，待处理及后续 Tool 审批均由服务端使用 `always-tool` 自动同意，普通 HumanGate 与用户信息交互不受影响。

### 执行 Agent 或 Agentflow

```mermaid
sequenceDiagram
    participant Client
    participant Hub as ExecutionHub
    participant Connection as ExecutionConnection
    participant Handler as ExecCommandHandler
    participant Context as ExecutionConnectionContext
    participant Factory as RuntimeFactory
    participant Runtime as Agent or Agentflow Runtime
    participant Pipeline as TurnPipeline
    participant Sink as MessageSink

    Client->>Hub: DispatchCommand(ExecCommand)
    Hub->>Connection: DispatchAsync
    Connection->>Handler: typed command
    Handler->>Context: StartTurnAsync
    Context->>Context: validate and resolve task/workspace
    Context->>Factory: StartAsync(turn snapshot)
    Factory->>Runtime: create or reuse
    Runtime->>Runtime: register ActiveTurn
    Factory-->>Context: RuntimeStartResult
    Context-->>Client: command accepted

    Runtime->>Pipeline: execute message stream
    Pipeline->>Sink: turn-start
    loop Runtime output
        Runtime-->>Pipeline: AgwMessage
        Pipeline-->>Sink: AgwMessage
    end
    Pipeline->>Sink: turn-finished
    Runtime->>Runtime: clear ActiveTurn
```

具体步骤如下：

1. `ExecCommandHandler` 校验 `agentId`，然后把命令交给 connection context。
2. Context 拒绝同一条 connection 上的并发 turn；没有 settings 时创建内置 project 的默认快照。
3. 首次执行通过 `ITaskAppService` 解析或创建 task，并通过 `IProjectAppService` 解析 workspace；后续 turn 复用两者。
4. target 改变时释放旧 runtime；同一 target 则尝试复用。
5. Context 从当前 connection 状态创建包含 settings、task、target、用户、workspace 和 message sink 的 `RuntimeTurnContext` 快照。
6. `RuntimeFactory` 确保 workspace 存在，并创建 `AgentRuntime` 或 `AgentflowRuntime`。
7. `RuntimeBase.StartTurn` 先注册 `ActiveTurn`，再启动实际执行，避免 turn 已运行但尚未对 interrupt 可见的竞态。
8. 后台任务进入 `RuntimeTurnContextAccessor` 作用域，并把输出交给 `TurnPipeline`。
9. turn 结束后，runtime 清理 `ActiveTurn`；runtime 本身仍留在 connection context 中，供下一轮复用。

Agent 执行结束时，`AgentRuntimeService` 会在 `finally` 中保存 SDK session state。外部 Codex Agent 还会通过 task-session binding 记录 provider session id，以支持后续恢复。

### Result Summary

Definition 创建的 System Agent 可通过 `EnableSummary` 在一次主执行成功后追加本轮总结。总结复用该 Agent 的 `ModelProviderId`，以一次性 `IChatClient` 调用执行；输入只包含本轮用户文字和本轮 Assistant 的 `TextContent`，不加载历史、工具或技能。External Agent 不支持该开关。

Agentflow 不读取内部 Agent 节点的 `EnableSummary`。流程总结只发生在显式 Output 节点：`ConfigJson.enableSummary` 为 `true` 时，流程必须只有一个 Output，并配置有效的 `SummaryModelProviderId`。传入总结模型的是流入 Output 的消息，Output 的 `Instructions` 会作为额外总结要求。

两种路径都保留原始输出并在末尾追加一条 `result`：

- `role = system`；
- `author = $agw-server`；
- 顶层 `additionalProperties.type = result`；
- `contents` 只有一个 `TextContent`。

总结文字可在有助于可读性时使用 Markdown（如标题、列表、强调或代码块），也可以保持纯文本；服务端除去首尾空白外不会改写模型返回的 Markdown。

总结为空或模型调用失败时仍返回 `Summary generation failed.`，不会使已成功的主执行失败；取消则继续向上传播。Summary 的 token usage 会累计到当前 project/context。`result` 会持久化供历史展示，但 `EfCoreChatHistoryProvider` 在构造下一轮模型历史时会过滤它。

### Interrupt、HumanGate 与用户信息交互

`InterruptCommandHandler` 只作用于当前活动 turn。`ActiveTurn.RequestInterrupt` 会先调用 runtime 专用 interrupt hook，再取消 linked cancellation source。没有活动 turn 时，服务端返回 system message。

Agentflow 进入 HumanGate、Tool 请求审批或 `HumanInteractionRequiredAIFunction` 请求用户输入后，`HumanGateApprovalCoordinator` 按 `requestId` 保存待处理响应。用户信息交互通过 `human-interaction-request` control message 携带来源 `toolName`/`callId`、`interactionKind` 和结构化 `payload`，客户端可将交互面板嵌入对应的 function call，并在 `HumanResponseCommand.responseData` 中返回结构化数据。`HumanResponseCommandHandler` 将响应转发给当前 `ActiveTurn`；request id 不匹配或已结束时返回 system message。

### 断开连接

```mermaid
stateDiagram-v2
    [*] --> ConnectedIdle
    ConnectedIdle --> Running: ExecCommand
    Running --> ConnectedIdle: turn finished
    Running --> Running: InterruptCommand
    ConnectedIdle --> Disposed: disconnect
    Running --> DetachedRunning: disconnect
    DetachedRunning --> Disposed: turn finished
    Running --> Interrupted: disconnect while waiting for HumanGate
    Interrupted --> Disposed: turn settled
```

断线不会直接取消普通运行中的 turn。`ExecutionConnection` 先标记为 detached，message sink 随后丢弃输出；后台任务继续完成持久化，空闲后再释放 connection scope。若断线时正在等待 HumanGate，由于客户端无法再响应，当前 turn 会被中断。应用关闭时，host cancellation token 会终止仍在执行的任务。

## 状态归属与并发约束

| 状态 | 所有者 | 说明 |
| --- | --- | --- |
| connection id 映射 | `ExecutionConnectionRegistry` | SignalR transport 生命周期 |
| attached、command gate、DI scope | `ExecutionConnection` | connection 生命周期与命令串行化 |
| settings、resolved task、workspace、target | `ExecutionConnectionContext` | 只能通过原子操作更新 |
| Agent/Agentflow runtime | `ExecutionConnectionContext` | 空闲 turn 之间复用，不向 handler 暴露 |
| SDK AgentSession | `AgentRuntime` | 由 `AgentSessionStateStore` 加载和保存 |
| 当前 turn | `RuntimeBase` | 同一 runtime 最多一个 |
| cancellation、interrupt hook | `ActiveTurn` | 一次执行独享 |
| 待处理的审批与用户交互请求 | `HumanGateApprovalCoordinator` | 每个 turn 独享 |
| settings/task/target/user/workspace/message sink 快照 | `RuntimeTurnContext` | AsyncLocal，只读、仅在 turn 内可见 |

`ExecutionConnection` 的 command gate 保护命令级状态变更，`RuntimeBase` 的 lock 保护活动 turn。两个锁解决的问题不同，不应合并：前者负责命令串行化，后者负责后台 turn 生命周期。

## Command 扩展

Execution command 是 compile-time 模块扩展，不做 assembly scanning 或运行时插件加载。新增 command 只涉及 contract、typed handler、一次 DI 注册和测试，不需要修改 `AgentRunCommand`、dispatcher 或 SignalR 配置。下面以 `StopCommand` 为例。

### 1. 创建 command 切片

在 `Commands/Stop/` 中新建 `StopCommand.cs`，contract 与后续 handler 使用同一个 namespace：

```csharp
using Agw.Agents.Execution.Commands.Abstracts;

namespace Agw.Agents.Execution.Commands.Stop;

public sealed class StopCommand : AgentRunCommand
{
    public string? Reason { get; set; }
}
```

客户端发送的 discriminator 将是：

```json
{
  "type": "StopCommand",
  "reason": "user requested"
}
```

### 2. 在同一目录实现 handler

在 `Commands/Stop/` 新建 `StopCommandHandler.cs`：

```csharp
using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Connections;

namespace Agw.Agents.Execution.Commands.Stop;

public sealed class StopCommandHandler : IExecutionCommandHandler<StopCommand>
{
    public Task HandleAsync(
        StopCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken) =>
        context.InterruptTurnAsync(command.Reason, cancellationToken);
}
```

handler 应保持薄，只做 command 校验/翻译并调用 context 的稳定操作。若新 command 需要新的 connection 能力，应先在 `ExecutionConnectionContext` 中设计一个能维护完整不变量的操作，而不是暴露字段、runtime 或 message sink。

一个 command 的 contract、handler 和直接测试应保持命名一致；只有真正被多个 command 共享的抽象才进入 `Commands/Abstracts/`。

### 3. 注册 DI

在 `Agw.Agents.DependencyInjection.AddAgents` 中增加：

```csharp
services.AddExecutionCommand<StopCommand, StopCommandHandler>(nameof(StopCommand));
```

这一次调用同时注册 handler 和 JSON 派生类型映射。command CLR type 或 discriminator 重复时，应用在构造 dispatcher/options 时失败，避免出现不确定分派或协议漂移。

### 4. 明确 command 的行为边界

实现前需要决定以下事项：

- 活动 turn 期间是否允许执行；由 context 操作统一执行 busy 规则。
- 是否修改 settings、resolved task、workspace、target 或 runtime；这些状态只能由 context 维护。
- 是否启动后台工作；执行 Agent/Agentflow 时应走 `RuntimeFactory` 和 `RuntimeBase.StartTurn`。
- 输出是 system message、error message，还是进入标准 turn 协议。
- command 是否需要加入客户端 contract 类型和前端调用封装。

不应在 handler 中使用 `IHubContext`、捕获 Hub、创建锁、解析 project/task，或直接操作 `RuntimeBase`。这些行为分别属于 transport adapter、connection context 和 runtime 模块。

### 5. 添加测试

至少覆盖：

- contract 的 JSON discriminator 和字段反序列化；
- dispatcher 能找到唯一 handler；
- handler 在 idle、running 和无 runtime 状态下的行为；
- connection context 的状态失效与 runtime 释放；
- 客户端可见消息及错误边界。

现有测试可作为入口：

| 测试 | 覆盖内容 |
| --- | --- |
| `ExecutionRequestsTests` | command JSON contract |
| `ExecutionCommandRegistrationTests` | typed handler/JSON 单一注册 seam 与重复 discriminator |
| `ExecutionCommandDispatcherTests` | handler 查找、未知命令、重复注册 |
| `ExecutionCommandHandlerTests` | handler 翻译与 connection context 状态规则 |
| `ExecutionConnectionTests` | idle/running/HumanGate 断线处理 |
| `RuntimeBaseTests` | 单活动 turn、中断和 AsyncLocal 作用域 |
| `RuntimeTurnContextAccessorTests` | 上下文恢复与并行隔离 |
| `TurnPipelineTests` | streaming、buffering 和终止状态 |
| `ExecutionHubContractTests` | SignalR Hub 的公开契约 |

运行 Execution 相关测试：

```bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj
```

如果修改了 `IAgentRuntimeService` 或跨模块 namespace，还需要运行：

```bash
dotnet test tests/Agw.A2A.Tests/Agw.A2A.Tests.csproj
dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj
```

## 非 SignalR 调用

A2A 和 Jobs 不经过 `ExecutionHub`、connection registry 或 command dispatcher。它们直接调用 `IAgentRuntimeService` / `IAgentflowRuntimeService`：

- `IAgentRuntimeService.ExecuteByIdAsync` 用于按 Agent id 执行；
- `IAgentRuntimeService.CreateRuntimeAsync` 可创建带 SDK session 的 `AgentRuntime`；
- `IAgentflowRuntimeService.ExecuteAsync` 用于非实时 Agentflow 执行。

因此，修改 Agent/Agentflow 构造、session 持久化或公共 service 接口时，需要同时检查 SignalR、A2A 和 Jobs；只修改 connection command 时，影响范围通常局限在实时执行链路。
