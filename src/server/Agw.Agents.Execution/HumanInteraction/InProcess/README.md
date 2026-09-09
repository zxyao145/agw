# InProcess 生命周期

`InProcessInteractionSession` 是一轮执行的 handler 与输入 channel，持有 sink、纯权限状态和 pending 集合。`RuntimeFactory` 创建并通过 AsyncLocal 作用域传递它；`ActiveTurn` 只转发类型化响应。

`ResolveAsync` 在锁内判断自动批准并登记 pending，再调用 sink 发布，最后等待响应。快速客户端可以在 `WriteAsync` 尚未返回时提交回答。调用方只提交请求，不需要掌握登记与发布的先后关系。

响应必须同时匹配 `InteractionId` 和请求变体。错误类型不会消费 pending；完成后重复响应不再接受。发布失败、等待取消、整轮结束都会清理对应等待。所有路径根据剩余集合数量更新连接状态，完成一个请求不会隐藏另一个请求。

`SetPermissionMode(FullAccess)` 与登记共享同一把锁，完成已有普通工具审批，保留 UserInput 和 HumanGate。SDK session 授权在下一次授权检查中同步，控制线程不修改 SDK 状态。

`InProcessAgentflowRunner` 同时消费工作流事件与人工响应任务。因此并行 HumanGate 可以同时显示、分别作答。任意 HumanGate 拒绝后终止工作流，并取消其他等待。用户输入取消只返回协议的取消结果；显式中断和等待期间的连接断开则取消本轮执行。

验证入口：`InProcessInteractionSessionTests` 覆盖快速响应、发布失败、部分取消、错误类型与重复响应；`AgentflowInteractionRegressionTests` 覆盖真实工作流的并行批准、拒绝和剩余等待清理。
