# Durable 暂停与恢复

Durable 把等待保存为 execution 状态，释放本段执行资源。普通 Agent 和 Agentflow 都通过 `DurableInteractionHandler` 返回显式 `Pending`；Agent 保存 session，Agentflow 保存 checkpoint。

## 请求如何保存

`HumanInteractionDescriptionChatClient` 使用本次模型调用的最终工具集合，通过 `IHumanInteractionProtocol` 生成输入描述。它不识别具体工具名。描述登记到 `InteractionRequestRegistry`，并暂存于 SDK 请求中；整个目录独立保存，因此 SDK 丢失附加属性或分次返回批量审批时仍可识别输入类型。

`DurableExecutionStore` 在同一状态记录中提交：

- manifest，包括权限模式和单调递增的权限版本；
- session / checkpoint；
- `DurableInteractionState.Pending`：当前人工边界；
- `InputCatalog`：本次 execution 已知的输入描述与稳定交互 ID；
- `ResolvedInputs`：之前分段已接受、后续工具仍可能需要的回答；
- 当前边界的响应集合。

提交成功后才通过统一 `InteractionMessageMapper` 发布尚未回答的请求。等待期间断开连接只结束订阅，持久 execution 保留。重新订阅按原始 `streamingScopeId` 和调用来源关联卡片。

## 响应与恢复

提交命令校验 execution 所有者、session generation、交互 ID 和响应变体，按当前权限归一后保存；当前边界的全部响应到齐后进入 `Resuming`。重复提交同一已接受决定是幂等的，冲突回答被拒绝。

Runner 恢复时再次应用权限规则。普通工具的决定转换成 MAF 单次审批响应；用户输入的审批 envelope 只用于恢复调用，即使用户取消也要进入输入工具的取消分支。HumanGate 拒绝终止整个工作流，其他 gate 的待答响应不再恢复。

`ResolvedHumanInteractionChannel` 要求明确的业务节点和 SDK call ID，并匹配工具名、输入类型、payload 与保存的响应 ID。找不到唯一匹配项就失败；它不会按工具名找第一条，也不会改写回答 ID。恢复重试只能复用同一逻辑交互的回答。

SDK 会等同一批审批全部收齐后才调用工具。因此跨后续人工边界必须保留之前接受的输入回答，直到 execution 结束；只保留“当前问题”会丢失批量输入。

## 动态权限

`SetPermissionModeCommand` 更新持久 manifest 与版本。切换 FullAccess 时只完成普通工具 pending，保留问答与 HumanGate。后到达的分段结果在落库时同样使用最新权限。

权限更新、人工响应接受和分段结果保存使用短时控制锁，避免归一与状态写入互相覆盖；这个锁不占用执行整段的 ownership lock。Running 期间更新权限不改变 Worker 的 ownership version。Worker 在每次授权检查前读取最新权限，并在 SDK 自身的执行路径中清理失效授权；已有轮询同时负责尽快发现中断。

## 格式与边界

本次直接替换交互快照格式，不包含旧消息兼容或旧 pending 迁移。启动 manifest 的项目与所有者格式保持 schema v1，仅在 settings 增加权限版本，避免影响原有数据的归属校验和清理。旧 pending 不承诺恢复；其他业务数据不清理，也没有数据库结构变更。本改动不增加外部 Agent 的 Durable 执行能力。

验证入口：`HumanInteractionRegressionTests`、`DurableInteractionStateTests`、`MafInteractionIntegrationTests`、`AgentflowInputIntegrationTests` 和 `AgentflowInteractionRegressionTests`。
