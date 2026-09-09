# 交互规则与编排

`IInteractionHandler.ResolveAsync` 接收三种数据请求，返回 `InteractionResolution.Resolved(Response)` 或 `Pending(Request)`。普通等待、自动批准、Durable 暂停均有明确结果；取消使用 `CancellationToken`，不把业务异常作为正常暂停信号。

`InteractionRules` 是纯规则：校验响应 ID、请求 / 响应类型，以及工具授权范围。用户输入的字段语义由工具协议校验。

| 权限模式 | 普通工具 | 已批准响应的有效范围 | UserInput / HumanGate |
| --- | --- | --- | --- |
| `FullAccess` | 自动批准 | `AlwaysTool` | 仍需人响应 |
| `AlwaysAsk` | 等待决定 | `Once` | 仍需人响应 |
| `AllowSameArguments` | 相同参数的已有授权可复用，否则等待 | `AlwaysArguments` | 仍需人响应 |
| 未指定 | 遵循工具能力声明及用户决定 | 保留合法提交范围 | 仍需人响应 |

工具被拒绝时范围统一为 `Once`，不产生授权。`HumanInteractionPolicy.Reject` 用于 Jobs 等无人值守执行：先检查普通工具能否自动批准，其余人工请求明确失败。

`InteractionPermissionState` 只保存模式、版本和执行作用域，不持有 SDK session。权限版本递增，因此 `A → B → A` 也会使旧授权失效。InProcess 控制命令直接更新该状态；Durable 命令先更新持久 manifest，Worker 的授权检查再读取最新版本。MAF Adapter 在 session 自身的执行路径上清理和使用授权。

`InteractionRequestRegistry` 保留输入协议描述。MAF 可能一次生成多个审批，却分次返回，并在内部队列持久化时丢弃自定义描述。目录以 SDK 请求作用域和请求 ID 为键保存每次逻辑输入；恢复不会重新生成交互 ID。不同节点使用相同工具和 call ID 仍是不同交互，重复使用关联键描述不同输入会失败。
