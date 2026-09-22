# 消息归并与持久化

流式分片是传输单位，逻辑消息是历史存储单位。每个 producer 的 `AgentMessageProjection` 保留消息身份和内容块；同一消息的文本增量在内存追加，Projects 在刷新边界读取完整内容并更新原行。

## 接入 Agent

1. 实现 `IAgentMessageAdapter<AgentResponseUpdate>`。文本和 thinking 使用 `AppendText`，完整工具块使用 `PutBlock`；只有明确的完整消息或状态更新才使用 `PutMessage`。`MapSnapshot` 处理历史回调，由适配器根据 SDK 的完成语义产生 `SealMessage`；`MapFinalization` 提交已收到结束信号的消息状态。
2. 给 `NormalizedHistoryAgent` 提供 adapter factory，每次调用创建独立实例。SDK 历史 callback 委托到同一个 `NormalizedChatHistoryProvider`，关闭独立写入。
3. 纯控制和 usage 继续原信号链路。Result 保持独立用途及消息 ID，通过 `resultSourceMessageId` 关联正文。
4. 添加协议 fixture，验证重复文本、空白、块边界、最终回调、生产者隔离、取消和跨 flush 单行更新。不以构造的 fixture 代替对原生协议的核实。

普通模型使用 MAF 调用边界和源消息 ID；Claude 使用原生 `StreamEvent` 块索引；Codex 的文本和 thinking 复用通用追加，不同 item ID 保持消息边界，工具/计划的 System item 状态按原行更新。Pi 提供块索引和共享的实时/历史 ID；当前 Agw 开启其完整结束消息输出，用于最终校准。适配器不访问 DbContext，不决定刷新周期。

## 存储与生命周期

- `IConversationMessageWriter.ScheduleAsync` 把投影交给现有执行历史缓冲。缓冲持有 Gate 时捕获并提交；没有执行缓冲时，同一 source 的捕获和写入也串行执行。
- `CapturePending` 只返回有变更的消息，并缓存不可变的提交对象。`Acknowledge` 仅在对象仍为当前捕获对象时清除脏标记。写入期间的新修改会使缓存失效，因此不会被旧提交确认清除。
- `UpsertAsync` 在现有会话写入锁和事务内验证 owner、generation、producer。同一规范 ID 更新同一行，保留任务关联、首次时间和历史顺序；重复写入同一内容不新增行。
- 不存储或传输消息修订号。顺序由单一归并器、提交 Gate 和会话写入锁保证，不支持绕过该链路并行提交同一消息的旧内容。
- 完整消息缺少可靠的源 ID 时独立保存，包括正文相同的多条消息。普通模型的无 ID 流式输出使用本次调用的身份，并通过规范消息元数据传递给完整回调。
- 消息封存之后保留原正文；同一身份再出现新的内容时开启下一条逻辑消息，各自占一行。批准恢复的那次调用会在同一批历史消息里重复出现同一个消息 ID 或工具 callId，这条规则使这些内容全部写入历史，并且不终止执行。
- Claude 历史回调更新消息内容，原生 `AssistantMessage` 和 `FinishReason` 提供完成依据。SDK 在回调中包含的补齐内容只处理一次；中断清理回调保留部分正文。收尾时保存已结束消息的状态，再将其余消息标记为中断或失败。Pi 的完整结束快照与历史回调共享身份，重复快照保持已处理状态。
- 完成状态保存在已有 JSON，未完成/失败内容仅供展示，不进入模型历史和 handoff。
- 三种 `ConversationHistory` 模式只影响提交时间。失败保留未确认内容；失去 owner/generation 时停止写入。
- 当前运行时不续传已中断的原生模型网络流，新调用使用新 producer；现有 durable 事件重放和执行所有权机制保持不变。

## 实时与历史

沿用 `ReceiveMessage(AgwMessage)` 和现有 HTTP 历史响应。`additionalProperties.messageOperation` 区分追加、块更新、完整校准和完成；元数据保留 `messageState`、`sourceMessageId`、`producerScopeId`、`conversationGeneration`，块身份为 `contents[].additionalProperties.blockId`。文本、工具、`UriContent` 和 `DataContent` 的 DTO 转换均保留内容块元数据。

`@agw/execution-core` 按规范消息 ID 和内容块 ID 应用操作，不按正文去重。SDK 完整回调使用替换以避免重复拼接。连接恢复沿用既有执行恢复与历史加载，不增加消息版本协商、缺版本重试或单消息快照接口。

不增加数据库列或迁移。旧碎片记录通过现有历史加载兼容读取，不自动回写。服务端和共享客户端归并逻辑需配套发布，旧客户端不保证理解新增操作元数据。

## 验证入口

- `AgentMessageProjectionTests`：归并、块边界、最终校准、精确提交确认。
- `NormalizedHistoryRegressionTests`：实际 SQLite 存储及已安装 SDK 的消息处理器，验证无 ID 完整消息、跨刷新归并、Claude 回调时序与中断状态、Pi 重复快照。
- `NormalizedConversationHistoryTests`、`StreamingConversationHistoryTests`：三种提交模式、SDK 回调、模型工具循环和取消。
- `ConversationMessageSnapshotTests`、`ConversationMessagePostgresTests`：串行写入、幂等、提交确认丢失、权限隔离和真实数据库单行更新。
- `message-operations.test.ts`、`message-delivery.test.ts`：共享客户端归并及原有 SignalR 通道交付。

指标为 `agw.history.normalize.duration`、`operations`、`flush.duration`、`write.failures`、`pending.bytes`、`pending.oldest_age`。不携带会话或用户标签，日志不输出消息正文。
