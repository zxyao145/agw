# Summaries：Agent 与 Agentflow 共用的结果摘要

`Summaries` 提供结果摘要能力，由单 Agent 执行和 Agentflow 的 Output 节点共同使用。因此它作为共享能力放在执行项目顶层，与 `Agents/`、`Agentflows/` 并列。

虽然服务名为 `AgentTurnSummaryService`，当前实际职责已经覆盖两类执行目标。两者复用同一个服务，摘要功能没有再拆成 Agent / Agentflow 两套实现。

## 文件职责

| 文件 | 职责 |
| --- | --- |
| [IAgentTurnSummaryService.cs](IAgentTurnSummaryService.cs) | 定义根据消息生成摘要结果的接口。 |
| [AgentTurnSummaryService.cs](AgentTurnSummaryService.cs) | 组织摘要提示词、调用模型、创建结果消息、写入历史并记录模型用量。 |
| [ISummaryChatClientFactory.cs](ISummaryChatClientFactory.cs) | 定义根据模型配置创建摘要 ChatClient 的接口。 |
| [SummaryChatClientFactory.cs](SummaryChatClientFactory.cs) | 读取模型、Provider 和认证配置，创建摘要调用所需的 ChatClient。 |

## Agent 与 Agentflow 如何使用

| 调用方 | 触发位置与消息范围 | 主要入口 |
| --- | --- | --- |
| Agent | 根据 Agent 的摘要配置，对本轮输入和输出生成摘要。 | [AgentRuntime](../Agents/Runtime/AgentRuntime.cs)、[AgentRuntimeService.Execution](../Agents/Runtime/AgentRuntimeService.Execution.cs) |
| Agentflow | 当 Output 节点启用摘要并具有摘要上下文时，对到达该节点的消息生成摘要，将节点 Instructions 作为附加要求。 | [AgentflowWorkflowCompiler.CreateOutputBinding](../Agentflows/Workflows/AgentflowWorkflowCompiler.cs) |

是否生成摘要、使用哪个模型、汇总哪些消息，由各自引擎决定。共享服务只接收完成摘要所需的数据：

```csharp
Task<ChatMessage> CreateResultAsync(
    Guid modelProviderId,
    IReadOnlyList<ChatMessage> sourceMessages,
    Guid projectId,
    string contextId,
    string? customInstructions,
    CancellationToken cancellationToken = default
);
```

接口没有 `AgentType`、Agent ID 或 Agentflow ID 参数，服务内部也不根据执行目标类型分支。这里与 [Turns](../Turns/README.md) 的区别是：Turn 上下文保存执行目标，而摘要服务消费调用方已经选定的消息和配置。

## 生成结果的流程

1. 通过 `ISummaryChatClientFactory` 创建摘要模型客户端。
2. 提取来源消息中的文本，结合默认摘要指令和可选的附加要求调用模型。
3. 将摘要包装为 `ChatRole.System` 消息，并设置 `AdditionalProperties["type"] = "result"`。
4. 通过 `IConversationHistoryWriter` 将结果追加到指定项目、上下文的历史中；模型返回用量时，通过 `IAgentUsageRecorder` 以 `$summary` 记录。
5. 返回结果消息，由 Agent 执行路径或 Agentflow Output 节点加入其输出。

历史和用量的具体持久化通过既有契约完成；摘要服务不管理执行回合、工作流路由或人工审批。共享服务本身也不根据 InProcess / Durable 执行方式分支。

## 与上下文压缩的区别

| 能力 | 目的 | 对消息的处理 |
| --- | --- | --- |
| `Summaries` | 整理用户请求、执行结果和未完成事项，形成可展示的结果摘要。 | 额外调用摘要模型，追加 `type=result` 消息，保留原始历史。 |
| Compaction | 在模型调用时控制上下文窗口大小。 | 调整发送给模型的上下文视图，由 Agent 的压缩策略和会话状态处理。 |

Compaction 的配置位于 [AgentRuntimeService.CreateDefinitionAgents](../Agents/Runtime/AgentRuntimeService.CreateDefinitionAgents.cs)，运行管线由 [AgwAgentExtensions.Tools](../Agents/Tools/AgwAgentExtensions.Tools.cs) 组装。完整执行边界见 [Execution README](../README.md)。
