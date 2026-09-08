# Approvals：权限策略、授权状态与 MAF 协议适配

本目录处理执行过程中的审批决策：是否需要人工、批准范围如何表达、权限变化怎样影响已有 session，以及 Agw 决策如何转换为 MAF 能消费的响应。InProcess 和 Durable 都复用这些能力。

整体交互类型与契约见 [HumanInteraction 总览](../README.md)。本文按“为什么需要审批层 → 权限判断 → 状态 → 协议转换 → 无人值守与执行差异”展开。等待机制分别见 [InProcess](../InProcess/README.md) 和 [Durable](../Durable/README.md)。

## 1. 为什么 HumanInteraction 下面有 Approvals

人工交互包含不同问题：工作流需要人确认是否继续，工具需要授权，模型也可能需要用户提供信息。Channel 负责取得回答，不能单独决定哪些请求应该自动批准，也不能直接表达 MAF 的长期授权。

本目录因此承担三项共享职责：

| 职责 | 主要实现 | 解决的问题 |
| --- | --- | --- |
| 审批策略 | `PermissionAwareApprovalHandler`、`UnattendedApprovalHandler` | 普通工具能否自动运行？无人值守时遇到人工输入怎么办？ |
| session 授权状态 | `PermissionModeState`、`ToolApprovalPermissionState` | 运行中切换模式后，旧的持续授权还能否生效？ |
| MAF 协议适配 | `ToolApprovalSupport` | 如何把 Agw 决策变成单次 / 长期 approval 响应，并穿过 workflow 端口？ |

MAF approval 在 Durable 中还承担暂停边界。`ask_user_question` 可以先表现为一个 MAF 工具审批请求，使 Runner 结束当前分段；恢复后仍要把真实答案注入交互工具。这不改变问答的语义，所以 FullAccess 不能替用户作答。

## 2. PermissionAwareApprovalHandler：包装一个实际处理器

[PermissionAwareApprovalHandler](PermissionAwareApprovalHandler.cs) 实现 `IHumanGateApprovalHandler`，持有内部 handler 和共享的 `PermissionModeState`。它还尝试将内部 handler 识别为 InProcess `HumanGateApprovalCoordinator`，用于动态放行已经挂起的工具请求。

因此它是执行层的策略包装器，包含对 InProcess 运行机制的明确适配；不是与所有运行细节完全无关的纯 Domain Policy。

### 2.1 哪些请求可以自动批准

`CanAutomaticallyApprove` 要求当前模式为 FullAccess，并满足 `IsToolApproval`：

```csharp
request.ToolApprovalRequest != null
    && request.Mode != "interaction"
    && ToolApprovalSupport.GetToolName(request.ToolApprovalRequest) != "ask_user_question";
```

由此得到：

| 请求 | FullAccess 是否自动批准 | 原因 |
| --- | --- | --- |
| 普通 MAF 工具审批 | 是 | 工具授权可由权限模式决定 |
| Agentflow HumanGate | 否 | 没有 MAF ToolApprovalRequest，是显式人工关卡 |
| `mode=interaction` | 否 | 需要用户提供信息 |
| `ask_user_question` | 否 | 工具名也被显式排除，即使请求载荷尚未被识别为 interaction |

`RequiresHumanResponse` 返回自动批准条件的反值。调用方据此决定是否展示请求；自动批准时，`WaitForApprovalAsync` 返回 `Approved=true`、`ApprovalScope="always-tool"`，不会调用内部 handler。

### 2.2 手工决策返回后的授权范围

不能自动批准时，包装器调用内部 handler。返回拒绝决策或非普通工具请求时直接保留决策；返回已批准的普通工具决策时，按当前模式归一范围：

| PermissionMode | 对进入该返回路径的批准决策 |
| --- | --- |
| `FullAccess` | `always-tool` |
| `AlwaysAsk` | `once` |
| `AllowSameArguments` | `always-arguments` |
| 未指定 | 保留内部决策的 `ApprovalScope` |

表格描述的是 handler 接收到决策后的行为。`AllowSameArguments` 不在此处自己比较参数；它返回 MAF 的相同参数持续授权，由后续 session 授权机制处理。

### 2.3 运行中切换模式

`SetPermissionMode` 先更新权限状态。切换到 FullAccess 时，若内部 handler 是 InProcess coordinator，则调用 `ApprovePendingToolRequests`，完成已经等待中的普通工具审批。

`WaitForApprovalAsync` 在调用内部 handler 建立等待后还会再次检查自动批准条件。这样，模式在首次检查与 pending 登记之间发生变化时，也能尝试释放新登记的等待。

这些动作只批准普通工具；不会完成 HumanGate 或问答 pending。切换回较严格模式则依靠 session 状态清理，使之前的持续授权不再继续绕过新策略。

## 3. PermissionModeState 与 ToolApprovalPermissionState

[PermissionModeState](PermissionModeState.cs) 保存当前可空权限模式，以及按对象引用区分的 `AgentSession` 集合。

- `Current` 用 `Volatile.Read` 读取编码值；0 表示未指定。
- `Register(session)` 在锁内登记 session，并立即应用当前模式。
- `Set(mode)` 在同一把锁内对所有登记的 session 应用新模式，再发布新的当前值。

这种共享状态用于同一 turn 中的 Agentflow 多个节点 session。新节点获取 session 后登记，即可与已经运行的节点使用同一权限模式。

[ToolApprovalPermissionState](ToolApprovalPermissionState.cs) 操作 `AgentSession.StateBag` 的两个 key：

| Key | 内容 |
| --- | --- |
| `Agw.ToolApproval.PermissionMode` | Agw 已经应用到该 session 的权限模式标记 |
| `toolApprovalState` | MAF 的工具审批状态，包括已有持续授权 |

`Apply(session, mode)` 的行为为：

1. 模式未变化：保留现有状态，避免每次注册都清掉有效授权。
2. 设置新模式：移除 `toolApprovalState`，再写入模式标记。
3. 恢复为未指定：如果存在 Agw 模式标记，移除授权状态与模式标记；没有标记则不清理未由这个标记管理的状态。

因此 `FullAccess → AlwaysAsk` 后，旧 `always-tool` 规则不会留在 session 中继续生效。运行时的适用 session 会经现有 session store 保存；这里的“always”属于 MAF session 授权语义，不是全局用户权限表。

## 4. ToolApprovalSupport：Agw 与 MAF 之间的转换

[ToolApprovalSupport](ToolApprovalSupport.cs) 集中转换工具请求、批准响应和客户端消息，供普通 Agent 与 Agentflow 的 Runner 共用。

### 4.1 从工具审批创建统一请求

`CreateRequest` 从 `ToolApprovalRequestContent` 读取工具名，将其放入一条 Assistant 消息，再构造 `HumanGateApprovalRequest`。

| 情况 | Mode 与 Prompt |
| --- | --- |
| 识别到 `ask_user_question` 的 questions 参数 | `interaction`，提示 Agent 需要用户提供信息 |
| 其他请求 | `tool-approval`，提示是否允许对应工具运行 |

工具名优先取 `FunctionCallContent.Name`，其他内容退回 CallId 或 `unknown`。`GetArguments` 将函数参数序列化、解析并 Clone 成独立 `JsonElement`，供跨 segment 保存。

`TryCreateInteractionPayload` 专门识别 `ask_user_question`，只提取 questions 与可选 metadata。模型提供的 answers 不会成为交互 payload。Durable mapper 利用这一转换同时避免保存原始问答参数中的伪造答案。

### 4.2 将决策转换为 MAF 响应

`CreateResponse` 首先处理拒绝，再按 trim 和小写后的授权范围分派：

| 决策 | MAF 调用 |
| --- | --- |
| `Approved=false` | `request.CreateResponse(approved: false)` |
| `always-tool` | `CreateAlwaysApproveToolResponse()` |
| `always-arguments` | `CreateAlwaysApproveToolWithArgumentsResponse()` |
| `once`、null 或未识别的范围 | `request.CreateResponse(approved: true)` |

未识别的范围退回单次批准，不会扩大成持续授权。`ResponseText` / `ResponseData` 不由这个方法绑定到工具参数：HumanGate 文本由 Agentflow mapper 处理，结构化问答由 HumanInteraction channel 与工具协议处理。

### 4.3 为什么 workflow 还需要一次包装恢复

Agentflow 的响应端口要求 `ToolApprovalResponseContent`，但持续授权响应使用 `AlwaysApproveToolApprovalResponseContent` 包装。`CreateWorkflowResponse` 因此：

1. 先调用 `CreateResponse`。
2. 单次响应直接返回。
3. 持续授权则取出 `InnerResponse`，在 `AdditionalProperties` 写入 `Agw.ToolApproval.WorkflowApprovalScope`，值为 `always-tool` 或 `always-arguments`。

进入节点 Agent 边界后，`RestoreWorkflowResponses` 找到这个属性，重建持续授权包装。它只在确实替换了内容时克隆对应消息，不改变其他消息。

```text
HumanGateApprovalDecision
    → CreateResponse
    → 持续授权包装
    → CreateWorkflowResponse：内层响应 + scope 属性
    → workflow 端口
    → RestoreWorkflowResponses：恢复持续授权包装
    → 节点 Agent session
```

如果只把持续授权展开为单次响应而不保留 scope，下游会丢失用户选择的授权范围。

### 4.4 客户端控制消息

`CreateMessage` 生成 `tool-approval-request`，附带请求 ID、节点、mode、prompt、工具名和可选 arguments JSON 字符串。Durable 路径会延迟发布这种运行时控制消息，等 pending 保存后由自己的 mapper 重新生成；它可以把问答快照生成 `human-interaction-request`。

## 5. UnattendedApprovalHandler：无人值守时明确结束等待尝试

[UnattendedApprovalHandler.Create(permissionMode)](UnattendedApprovalHandler.cs) 返回：

```text
PermissionAwareApprovalHandler
    └── UnattendedApprovalHandler
```

普通工具在 FullAccess 下由外层自动批准；其余需要人工的请求进入内层并抛出 `AgentExecutionFailed`，错误指出对应工具或节点不支持无人值守人工交互。

这支持 Jobs 等入口的明确组合：FullAccess 允许普通工具，`HumanInteractionPolicy.Reject` 拒绝需要真实用户输入的边界。权限模式与是否允许人工参与是两个设置，不能互相替代。

没有交互 channel 的工具还可能在 `HumanInteractionRequiredAIFunction` 内部直接失败；该错误路径与无人值守审批 handler 一起覆盖不同的请求入口。

## 6. InProcess 与 Durable 复用到哪一步

| 路径 | 如何使用本目录 | 手工回答到达后的路径 |
| --- | --- | --- |
| InProcess 交互式执行 | 权限包装器包裹 coordinator；共享 session 模式状态 | coordinator 返回决策，权限包装器归一普通工具批准范围 |
| Durable 普通 Agent | 权限包装器包裹 capture handler | capture 时已经退出；新 segment 直接把持久决策转换成 MAF 响应 |
| Durable Agentflow | 先用权限 handler 判断自动批准 / 无人值守错误；其余请求形成 pending | Runner 按请求 ID 匹配持久回答，直接创建 workflow 响应 |

因此，Durable 保存的手工 `ApprovalScope` 在恢复时不会重新经过上文的模式归一返回路径。不能因为使用了同一个权限包装器，就推断所有执行模式的手工回答处理完全相同。

实时模式切换同样有边界：[ExecutionConnectionContext.SetPermissionModeAsync](../../Inbound/Connections/ExecutionConnectionContext.cs) 更新连接 settings，并把变化发给当前 InProcess Runtime。Durable 使用已登记 manifest 中的权限快照，这个命令不会修改该持久快照，也不会调用数据库版的 pending 批量批准。

## 7. 验证入口

| 测试文件 | 可核对的行为 |
| --- | --- |
| [PermissionAwareApprovalHandlerTests](../../../../../tests/Agw.Agents.Tests/PermissionAwareApprovalHandlerTests.cs) | 自动批准范围、手工范围归一、动态切换、旧授权清理、问答不被自动批准 |
| [HumanGateApprovalCoordinatorTests](../../../../../tests/Agw.Agents.Tests/HumanGateApprovalCoordinatorTests.cs) | scope / ResponseData 传递、空范围回退、问答 payload 排除模型答案 |
| [AgentflowRuntimeCharacterizationTests](../../../../../tests/Agw.Agents.Tests/AgentflowRuntimeCharacterizationTests.cs) | Durable 恢复 once / always-tool / always-arguments，以及无人值守 FullAccess 行为 |
| [AgentflowRuntimeServiceTests](../../../../../tests/Agw.Agents.Tests/AgentflowRuntimeServiceTests.cs) | 人工节点与工具审批的执行链、输出和会话行为 |

修改权限逻辑时，应同时检查已有 pending、新请求、已登记 session、新节点 session 和 Durable 恢复路径。请求获得自动批准与用户提供了有效答案是两个条件，不能用一个 `Approved=true` 取代答案校验。
