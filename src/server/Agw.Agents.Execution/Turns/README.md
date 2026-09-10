# Turns：Agent 与 Agentflow 共用的执行回合

`Turns` 管理一次用户输入触发的外层执行回合。执行目标可以是单个 Agent，也可以是整个 Agentflow；一个回合可以包含多次模型调用、工具调用或工作流节点执行。

`Agents/` 存放单 Agent 专属实现，`Agentflows/` 存放工作流专属实现。回合的任务跟踪、取消、上下文和输出协议由两者共用，因此 `Turns` 作为共享能力放在顶层，目录内部也没有再复制两套 Agent / Agentflow 实现。

## 文件职责

| 文件 | 职责 |
| --- | --- |
| [ActiveTurn.cs](ActiveTurn.cs) | 跟踪本轮执行任务、取消源和中断、人工响应、权限切换回调；自身不保存 Agent / Agentflow 类型。 |
| [RuntimeTurnContext.cs](RuntimeTurnContext.cs) | 保存本轮的执行目标、设置、任务、用户、工作区和消息输出上下文。 |
| [RuntimeTurnContextAccessor.cs](RuntimeTurnContextAccessor.cs) | 使用 `AsyncLocal` 暴露当前异步执行作用域中的回合上下文，并在作用域释放时恢复之前的值。 |
| [TurnPipeline.cs](TurnPipeline.cs) | 统一处理开始、输出、错误、取消和结束消息，以及非流式输出缓冲；不按 Agent / Agentflow 类型分支。 |
| [TurnMessageFactory.cs](TurnMessageFactory.cs) | 创建回合开始、结束及状态消息。 |
| [TurnMessageProtocol.cs](TurnMessageProtocol.cs) | 提供回合消息类型、结束标识和结束状态的协议访问方法。 |

## 如何区分 Agent 与 Agentflow

运行时通过 `RuntimeTurnContext.Target` 区分执行目标。`Target` 的类型是 [ExecutionTarget](../Inbound/Connections/ExecutionTarget.cs)，上下文提供两个便捷属性：

```csharp
public Guid AgentId => Target.AgentId;
public AgentRuntimeType AgentType => Target.AgentType;
```

| `AgentType` | 执行目标 | `AgentId` 的含义 |
| --- | --- | --- |
| `AgentRuntimeType.Agent` | 单 Agent | Agent 定义 ID |
| `AgentRuntimeType.Agentflow` | 整个 Agentflow | Agentflow 定义 ID |

这两个属性描述外层回合的目标。判断 ID 的含义时必须同时读取 `AgentType`，不能仅凭 `AgentId` 属性名将其视为单 Agent ID。

在具有回合上下文的代码中，通过注入的 `IRuntimeTurnContextAccessor` 读取：

```csharp
var context = turnContextAccessor.Current;
if (context is { AgentType: AgentRuntimeType.Agentflow })
{
    var agentflowId = context.AgentId;
}
```

`Current` 可能为 `null`：只有建立了回合上下文的异步作用域才有当前值，不能在普通请求或独立后台任务中假定它存在。`Push` 是执行程序集内部操作，调用方通过只读接口读取上下文。跨模块的 `ICurrentAgentTurn` 仅提供项目和用户快照，不暴露目标类型或 ID。

## 分派与共享边界

交互式启动统一经过 `IExecutionStarter`。其中 [InProcessExecutionStarter](../Runtimes/InProcess/InProcessExecutionStarter.cs) 建立 `RuntimeTurnContext`，再由 [RuntimeFactory](../Runtimes/InProcess/RuntimeFactory.cs) 根据目标类型选择 `AgentRuntime` 或 `AgentflowRuntime`：

- `ExecuteAgentAsync` 调用单 Agent 执行服务，再交给 `TurnPipeline.RunAsync` 输出。
- `ExecuteAgentflowAsync` 调用 Agentflow Runtime，也交给 `TurnPipeline.RunAsync` 输出。
- 两条路径都通过同一个 `StartTurn` 调用 [RuntimeBase](../Runtimes/RuntimeBase.cs)，注册 `ActiveTurn` 并建立回合上下文。

因此，目标类型保存在 `RuntimeTurnContext` 中，具体引擎由 RuntimeFactory 分派；`ActiveTurn` 和 `TurnPipeline` 处理共同的生命周期和协议，不各自重复保存目标类型。

人工交互的等待、审批策略和持久回答恢复归 [HumanInteraction](../HumanInteraction/) 所有。Durable 执行的持久身份、状态机和分段协调归 [Runtimes/Durable](../Runtimes/Durable/) 所有，不以连接中的 `ActiveTurn` 作为持久执行状态。整体执行链路见 [Execution README](../README.md)。
