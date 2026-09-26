# 消息追加与持久化

流式消息统一使用 Append 语义。每个 producer 的 `AgentMessageProjection` 按消息身份累积正文；相同文本块的增量按照到达顺序追加，工具和媒体内容按出现顺序保留。Projects 在持久化边界读取完整内容，更新同一个 MessageId 对应的历史记录。

## 接入 Agent

1. 实现 `IAgentMessageAdapter<AgentResponseUpdate>.Map`，返回当前更新中新增的内容，并保留消息及内容块身份。控制事件和 usage 继续走各自的传递路径。
2. 给 `NormalizedHistoryAgent` 提供 adapter factory，每次调用使用独立实例。SDK 的历史 callback 委托给同一个 `NormalizedChatHistoryProvider`。
3. 流式调用的历史正文来自已经观察到的增量。SDK 完整回调只参与调用收尾；后续到达的新增内容继续追加。非流式调用按消息顺序保存完整响应。
4. Result 使用独立的消息身份，通过 `resultSourceMessageId` 关联正文。

普通模型使用 MAF 调用边界和源消息 ID；Claude 使用原生 `StreamEvent` 的内容块索引；Codex 按 item ID 保持消息边界；Pi 为增量和补充内容提供内容块索引。适配器负责原生事件映射，Projects 负责数据库写入和刷新周期。

## 历史存储

- `IConversationMessageWriter.ScheduleAsync` 将累积内容交给执行历史缓冲。缓冲持有 Gate 时捕获并提交；同一个 source 的捕获和写入串行执行。
- `CapturePending` 返回有变更的消息，并缓存不可变的提交对象。`Acknowledge` 仅在对象仍为当前捕获对象时清除变更标记，保留写入期间到达的新内容。
- `ConversationMessageSnapshot.Message` 是捕获时构造好的 `ChatMessage`，捕获后不再修改。Projects 用自己的序列化选项写出一次，执行缓冲按快照对象复用转换结果，读取与提交反复捕获未变化的消息时不再重复序列化。
- 投影对流式增量按类型复制内容：文本与推理直接复制可序列化属性，并使用新的属性字典与注释列表；其他内容经过一次 JSON 往返。消息头只复制字典，属性值保持原始类型。
- `UpsertAsync` 在会话写入锁和事务内验证 owner、generation、producer。同一个 MessageId 更新同一行，保留任务关联、首次时间和历史顺序。
- 每条非流式完整消息独立保存。普通模型没有提供 MessageId 时，流式输出使用本次调用内的稳定身份。
- 取消和失败时保存已收到的正文。模型历史根据内容用途和工具调用配对规则过滤内容；执行状态由既有 turn 和 execution 管理。
- 模型历史读取只查询行 Id、序号、载荷与创建时间。一次 Agent 运行内每次模型调用都会重新读取历史，载荷未变化的行复用已经反序列化的消息；交给模型请求的是复制出的消息对象、内容列表与属性字典。
- 三种 `ConversationHistory` 模式只控制提交时间。失败保留未确认内容；失去 owner 或 generation 时终止写入。

## 实时交付

`ReceiveMessage(AgwMessage)` 传递新增内容。`additionalProperties` 保存 `sourceMessageId`、`producerScopeId`、`conversationGeneration` 等来源信息；内容块使用 `contents[].additionalProperties.blockId` 标识。文本、工具、`UriContent` 和 `DataContent` 的 DTO 转换保留内容块元数据。

`@agw/execution-core` 按消息身份追加内容。相同 blockId 的文本和 thinking 可以交错到达，分别累积到对应内容块。历史加载与实时更新使用一致的消息身份。连接恢复继续使用执行恢复和历史加载。

服务端 `StreamingMessageMerger` 按与 `appendStreamingContents` 相同的规则合并同一消息相邻的文本与推理增量：进程内 `TurnBroadcast` 合并 50 ms 窗口内的增量，Distributed 的 `DurableEventSink` 合并同一提交批次内的增量。客户端收到合并结果与依次收到各个增量得到相同的状态。

Agentflow 节点通过 Framework 更新流传递新增内容。Runner 记录已经交付的消息身份，完整响应和 Workflow 输出仅交付尚未发送的消息。节点输入通过独立的输入观察通道传递。

## 验证入口

- `AgentMessageProjectionTests`：追加内容、块边界、生产者内的消息身份及持久化确认。
- `StreamingMessageMergerTests`、`TurnBroadcastTests`：服务端增量合并规则、合并窗口与写入顺序。
- `NormalizedHistoryRegressionTests`：真实 SQLite 和 SDK 消息处理器，覆盖非流式完整消息、Claude 回调顺序和 Pi 结束事件。
- `NormalizedConversationHistoryTests`：三种提交模式、交错生产者、历史回调、后续内容及取消时的正文保留。
- `ConversationMessageSnapshotTests`、`ConversationMessagePostgresTests`：串行写入、权限隔离和数据库单行更新。
- `AgentflowMessageMapperTests`：增量顺序及完整响应的交付。
- `message-append.test.ts`、`message.test.ts`：客户端的内容追加与消息身份。

持久化指标包括 `agw.history.flush.duration`、`write.failures`、`pending.bytes`、`pending.oldest_age`。
