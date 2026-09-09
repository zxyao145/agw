# MAF 适配

`MafApprovalAdapter` 将 SDK approval 请求转换成三类交互中的工具审批或用户输入，并把对应决定转换回 SDK 单次响应。HumanGate 的工作流数据由 `AgentflowMessageMapper` 处理。客户端卡片始终由 Outbound 的 `InteractionMessageMapper` 生成。

MAF 的工作流请求端口来自 executor binding ID，可能含节点名称和字符归一，不等于业务 `NodeId`。`AgentflowNodeScopedAgent` 通过本适配层保存 `ProviderScopeId`，输入目录按该作用域和 `ProviderRequestId` 精确查找，同时保留业务节点路径和 SDK `CallId`。后续 SDK 批量请求即使丢失协议描述，也能找回原始交互。

持续授权保存在 Agw 自有的 `Agw.ToolApproval.Grants` session 状态中。`MafApprovalGrantAgent` 在 SDK 调用入口记录经过权限归一的授权，`MafSessionApprovalState` 在授权检查时比较工具名和语义 JSON 参数。有效期属于 session；权限模式或同一 execution 内的权限版本变化都会使旧授权失效。

不删除或重建 SDK 的 `toolApprovalState`：其中还包含请求队列和已经收集的回答，清理整个状态会破坏恢复。Agw 不生成 SDK 的长期授权 wrapper；需要持久范围时，在单次响应上携带 Agw scope，由适配层记录。这样工作流端口无需展开、再重建 SDK wrapper。

输入工具不能被已有工具授权或能力自动批准规则代替。`HumanInteractionDescriptionChatClient` 先登记输入身份，MAF 的自动审批规则遇到该身份就返回需人工响应。
