# ChatHttpClient、通信事件订阅与对话创建方规则

## 名词解释

- **ChatHttpClient**：`@agw/chat-runtime` 中统一处理对话通信的客户端。对话与 Agw Server 之间的 SignalR 执行通道（`/api/hubs/exec`）和对话 HTTP 接口都经它发出；每一次请求、请求的结果和服务端返回的每一条消息，都作为通信事件派发给订阅方。
- **通信事件（`ChatEvent`）**：ChatHttpClient 派发的事件，共三种：
  - `request`：ChatHttpClient 发起的一次 HTTP 请求或一次 hub 方法调用；
  - `response`：该请求的结果，成功时带解包后的返回值，失败时带错误；
  - `message`：服务端返回的一条 `AiMessage`，来自 `ReceiveMessage` 推送或历史分页响应。
- **通道（channel）**：`execution` 表示 SignalR 执行通道，`http` 表示对话 HTTP 接口。
- **消息来源（source）**：message 事件的来源。`live` 是服务端通过 `ReceiveMessage` 推送的消息，包括断线重连或刷新页面后服务端按 cursor 补发的消息；`history` 是历史分页响应中的消息。推送消息与历史消息可以分别订阅。
- **作用范围（`ChatScope`）**：事件所属的 `serverId`、`projectId`、`conversationId`。`serverId` 与 `ExecutionSessionManager` 会话键中的值相同：Web 为 `browser`，Desktop 为当前 Server profile 的 id，Mobile 为已验证 Server profile 的 id。
- **发起方（`ChatEventOrigin`）**：`client` 表示 Agw 客户端自身；`extension` 表示 Desktop 宿主代 extension 发出的 HTTP 请求或通过 execution API 创建的执行会话，带 `extensionId`。
- **订阅条件（`ChatEventFilter`）**：按事件种类、通道、发起方、作用范围、消息来源、消息角色、消息类型、content 类型选择事件。空条件 `{}` 表示全量订阅。
- **消息角色**：`AiMessage.role`，取值为服务端 `AiRole` 的 `system`、`assistant`、`user`、`tool`。
- **消息类型**：`AiMessage.additionalProperties.type`，取值见服务端 `AgwMessageTypes`，例如 `agw-turn-start`、`agw-turn-finished`、`interaction-request`、`permission-status`、`result`。模型输出的普通消息没有这个字段。
- **content 类型**：`AiMessage.contents[].type`，取值见 `@agw/api` 的 `MessageContentType`，例如 `TextContent`、`FunctionCallContent`、`UsageContent`。
- **已结算 Turn 与未结算 Turn**：Turn 进入终态（`Completed`、`Failed`、`Interrupted`），并且已经到达它的用量结算时间（`usage_settles_at`）时为已结算，其余为未结算。InProcess 模式与 worker 正常提交结果时，结算时间等于结束时间；Distributed 中断时 worker 可能仍在运行，结算时间在 worker 退出之后（5.1 节）。
- **usage 基线**：对话详情接口 `GET /api/projects/{projectId}/conversations/{conversationId}` 返回的 `usage`，是该对话已结算 Turn 与不属于 Turn 的用量之和；同一响应的 `usage.unsettledTurnIds` 列出没有计入的未结算 Turn。
- **连接身份（`ChatConnectionIdentity`）**：一次通信所用连接的 `baseUrl` 与 Bearer token。HTTP 请求取发出时的值，执行会话取创建时的值。它只交给进程内的订阅函数，不出现在事件中（1.2 节）。
- **宿主（host）**：指不经过 extension 的一方，包括 Desktop 自己的界面（Chat、Agent 与 Agentflow 的执行抽屉）、Web、Mobile，以及在 Server 内创建对话的 Jobs 与 A2A。需求中的“主进程”“主程序”都指宿主。
- **extension**：Desktop 中运行的 extension，经宿主的 `ExecutionService` 与 `ChatHttpClient` 使用执行会话，以 manifest 的 `id`（3 到 64 个小写字母、数字与单个连字符）标识。`id` 与去掉首尾空白的 `name` 都不能忽略大小写等于 `host`。
- **请求方（requester）**：一条执行连接所代表的宿主或某个 extension。宿主连接的 `ExtensionId` 为 null，extension 连接的 `ExtensionId` 为该 extension 的 `id`。
- **已受理的 Turn**：受理事务写入 `project_conversation_turn` 的 Turn 行。受理之后执行失败的 Turn 也算已受理；受理之前失败的请求不写入 Turn 行，例如 Agent 不存在、权限模式不支持。清空记录会删除对话的全部 Turn 行。
- **创建方（creator）**：保存在 `project_conversation.extension_id`，值为 `host` 或 extension 的 `id`，不可为空。第一个 `ExecCommand` 解析任务、插入对话行时记录请求方，与这一轮之后是否受理成功无关。A2A、执行之外写入历史与 Jobs 创建的对话记录为 `host`；extension 在开放对话中解析任务时记录为该 extension。
- **开放对话**：创建方为 `host`、`generation` 为 0、没有已受理 Turn 的对话，所有 extension 都可以读写。清空记录是唯一把 `generation` 加 1 的操作。宿主或 Job 的第一个 Turn 受理失败、执行之外写入历史、A2A 只保存任务快照时创建的对话都可能满足开放条件。
- **草稿对话**：客户端已经生成 `conversationId`、Server 还没有保存的对话。第一个 `ExecCommand` 解析任务时插入它并记录创建方。
- **写入操作**：改变对话或其运行中 Turn 的操作，包括发送消息（每一轮 `ExecCommand`）、恢复 Agentflow checkpoint（`ResumeCheckpointCommand`）、中断 Turn（`InterruptCommand`、`RecoverInProcessExecution(connectionId, true)`）、回答人工交互（`HumanResponseCommand`）、切换 Agent 模式（`SetModeCommand`）、切换权限模式（`SetPermissionModeCommand`）。
- **只读**：请求方不能执行上述写入操作。仍然可以配置会话（`SettingCommand`）、创建执行会话、订阅 Turn（`SubscribeTurnCommand`）、查询 checkpoint（`GetAgentflowCheckpoints`）与进程内执行（`FindInProcessExecution`、`RecoverInProcessExecution(connectionId, false)`），以及使用 `chat.observe`。extension 会话可以接收这台 Desktop 收发的该对话实时消息（8.6 节）。

## 背景

- **产品需求**：
  - 把 Chat 与 Agw Server 之间的通信统一到一个客户端 ChatHttpClient。
  - 其他组件与 Desktop extension 可以订阅这些通信：ChatHttpClient 发起的请求，以及服务端响应中的任意消息；可以按消息角色、消息类型、content 类型订阅，也可以全量订阅。
  - Token usage 面板改为根据 ChatHttpClient 中的 `UsageContent` 计算。
  - extension 经 ChatHttpClient 发出请求时，宿主识别发出请求的 extension，并把该身份传给 Server。
  - Server 记录对话创建方。宿主可以继续任何对话；extension 可以写入自己创建的对话与开放对话，对其他对话只读。
- **Token usage 现状**：
  - Web 与 Desktop 的 `packages/chat/src/ui-web/components/message/chat.tsx` 中，`Chat` 用 `conversationUsage` state 保存用量。初始值为 `sessionSeed.usage`：`ChatWorkspace` 打开对话时取对话详情的 `usage`，Agent 与 Agentflow 执行抽屉传入 `EMPTY_TOKEN_USAGE`。`applyExecutionMessage` 对收到的每条执行消息调用 `getMessageTokenUsage` 并累加；恢复 Agentflow checkpoint 后，`calculateConversationUsage(retainedMessages)` 按保留下来的消息重新计算。`ChatAside` 显示这个 state。
  - 服务端按 Agent 的每次运行写入用量：`AgentTelemetryMiddleware` 在运行的流结束时，把观测到的 `UsageContent` 合计写入 `agent_usage`，记录只有 `ProjectId`、`ContextId`，没有所属 Turn。一个 Turn 中可能有多次运行：`AgentTurnExecutor` 的每一轮工具审批、Agentflow 的每个节点、Durable 模式下等待人工输入前后的每一段，所以 Turn 运行期间读取的对话详情可能已经包含该 Turn 的一部分用量。
  - `AgentMessageAdapters.Records` 不把 `UsageContent` 写入历史，因此历史分页中没有 `UsageContent`，只有 `ReceiveMessage` 推送中有。
  - 有些用量不经过推送：`AgentTurnSummaryService` 用非流式调用生成 summary，用量直接写入 `agent_usage`，返回的 Result 只含文字；`resultOnly` 开启时，`ResultOnlyMessageSink` 只转发 result、控制与错误消息，客户端收不到 `UsageContent`。
  - Distributed 中断时，`DurableExecutionCoordinator.InterruptAsync` 在加锁的事务中直接写入 `Interrupted`、Turn 结局与结束事件，之后才取消本实例上的执行（`CancellationTokenSource.CancelAsync` 不等待执行退出）；其他实例上的 worker 要到下一次续租失败（`LeaseRenewSeconds`，默认 10 秒）才停止。worker 停止时 telemetry 的 `finally` 才写入用量，晚于结束事件。
  - 刷新页面后，`ExecutionSession.configure` 从保存的 durable cursor 之后恢复订阅，cursor 之前已经收到的消息不会再次推送。
  - Distributed 模式下刷新页面时，`ChatWorkspace` 先读取对话详情，`Chat` 再用保存的 cursor 发送 `SubscribeTurnCommand`，服务端补发 cursor 之后的消息，Turn 已经结束时同样补发（`DurableExecutionAttachment.AttachAsync`）。断线期间结束的 Turn，其用量既在详情中，又随补发的 `UsageContent` 到达，现在的 `Chat` 会把它再加一次。切换回运行中的对话时，现在的 `Chat` 用新读取的 `usage` 替换已经累加的值，该 Turn 已推送的用量随之丢失。
  - `packages/chat-runtime/src/conversation-controller.ts` 的 `ConversationController` 有一份相同的累加逻辑，仓库中没有调用方。
  - Mobile 不显示 Token usage。
- **通信分散在多处**：
  - SignalR：`ExecutionSession` 在四处被直接创建：`ExecutionSessionManager`（Web 与 Desktop 的 Chat）、`NativeExecutionSession`（Mobile）、Desktop 宿主的 `ExecutionService`（extension 的 execution API）、`ConversationController`。
  - HTTP：`chat.tsx` 直接调用 `apiGet("/api/agents/permission-capabilities")`、`apiGet("/api/agents/suggestions")`，以及 `@agw/projects` 的 `getConversationTurnInputs`、`getProjectConversationMessages`、`clearProjectConversationRecords`；`ChatWorkspace` 调用 `getProjectConversationDetails` 与 `getProjectConversationMessages`；Mobile 通过 `createProjectConversationService(client)` 与 `client.apiGet` 调用同一组接口。
  - 没有可以订阅这些通信的位置。extension 的 execution API 只能收到自己创建的会话的消息；Token usage 这类面板只能写进 `Chat` 的消息处理代码中。
- **请求方与创建方现状**：
  - 宿主与 extension 使用相同的 token 与 hub 地址，Server 无法区分请求方。extension 可以用任意 `conversationId` 继续聊天。
  - 进程内模式下，`RecoverInProcessExecution(connectionId, true)` 只校验两条连接属于同一用户，extension 的会话可以停止宿主正在运行的 Turn。
  - 进程内模式没有订阅其他连接 Turn 的方式；Distributed 会话不知道其他连接正在运行的 Turn，SDK 的 `onMessage` 只接收自己会话的 `execution.message`。

## 设计目标

### 功能目标

1. ChatHttpClient：Web、Desktop、Mobile 的 Chat 通过它使用 SignalR 执行通道与对话 HTTP 接口。
2. 通信事件与订阅：`request`、`response`、`message` 三种事件；订阅条件支持事件种类、通道、发起方、作用范围、消息来源、消息角色、消息类型、content 类型，推送消息（`live`）与历史消息（`history`）可以分别订阅，空条件为全量订阅。
3. Token usage：以 Turn 为统计边界。`ConversationTokenUsageStore` 订阅 ChatHttpClient，以对话详情中已结算 Turn 的用量为基线，加上未结算 Turn 推送的 `UsageContent`（按 `turnId` 与 `turnSequence` 去重）；每个 Turn 结束后在原连接下重新读取对话详情，Turn 已结束但用量尚未结算时继续读取；未结算 Turn 的推送用量保存在本地，刷新页面后恢复。
4. Mobile 改为通过 ChatHttpClient 通信。
5. Desktop extension：新增高风险权限 `chat.observe` 与 `api.chat.observe`，订阅当前 Server 上全部对话的通信，只接收属于当前连接身份的事件；extension 的 execution API 创建的会话也由 ChatHttpClient 创建，事件的发起方为该 extension。
6. 服务端：`agent_usage` 记录所属 Turn，Turn 记录用量的结算时间；对话详情的 `usage` 只计已结算 Turn 与不属于 Turn 的用量，并返回 `unsettledTurnIds`。
7. 请求方身份：宿主在 extension 会话的每个 `SettingCommand` 中写入 `extensionId`，宿主自己的会话省略该字段。HTTP 方法接受 `origin`，宿主代 extension 发出的请求标为该 extension。
8. 创建方与写入规则：`project_conversation.extension_id` 保存创建方；extension 成为创建方后保持不变，清空记录保留创建方。Server 在每个写入操作之前核对请求方、创建方与开放条件，拒绝时任何数据都不改变。
9. 公开创建方：对话详情与列表增加 `extensionId`、`openToExtensions`，extension 执行会话增加 `readOnly`；Web、Desktop 与 Mobile 的对话列表在标题前显示创建方图标。
10. 只读会话消息与错误：Desktop 把本机其他会话收发的同一对话实时消息转发给 extension 会话；拒绝写入时 Server 返回 `ErrorCodes.ConversationReadOnly`（403_0008），extension 收到 `ERR_CONVERSATION_READ_ONLY`。

### 技术目标

- **安全**：
  - `chat.observe` 为高风险权限：批准弹窗用警告色列出，broker 在每次 `chat.observe` 调用时检查。
  - 宿主只把属于当前连接的事件发给 extension：`scope.serverId` 等于当前 profile id，连接身份的 `baseUrl`、`token` 等于 `ContextService` 当前连接标识中的值。同一 profile 修改地址或更换身份后，旧配置下的会话与请求产生的事件不再发送。
  - 对话详情的同步请求只在对话的连接身份与当前 HTTP 连接身份相同时发出，不会用另一个 Server 的地址与凭据查询，也不会把另一个 Server 的数据写入这个对话的记录（5.3 节第 9 条）。
  - 发给 extension 的事件去掉 `SettingCommand.environmentVariables`。Context API 不提供环境变量，订阅同样不提供。
  - 事件中没有请求头与凭据：HTTP 事件只含方法、路径模板、参数与请求体；SignalR 的 Bearer token 由 `accessTokenFactory` 提供，不出现在 hub 方法参数中。
  - 订阅方收到深度冻结的副本，无法修改 Chat 正在使用的消息与请求对象。
  - extension 身份由宿主写入：`ExecutionService` 使用 broker 连接记录的 `extensionId`，extension 只能经宿主访问 Server，无法取得 token。Server 信任已认证连接报告的身份。
  - Desktop 校验 extension 的 `id` 与 `name`，保留名称 `host`，校验失败的 extension 不会加载；Server 校验 `SettingCommand.extensionId` 的格式与保留名称。两处身份校验保证 extension 的 `extensionId` 不等于宿主创建方 `host`，`IsCreatedBy` 的序号相等检查因此不会把 extension 识别为宿主创建方。`name` 的保留规则防止 extension 以 `host` 作为显示名称。
  - broker 只接受小写 UUID 格式的 `projectId` 与 `conversationId`，确保详情请求到达对应接口，并使消息作用范围可以按字符串匹配。
  - Server 在写入前执行规则检查；`readOnly` 用于 extension 提前显示状态。拒绝时标题、更新时间、历史、checkpoint 与 Durable 状态保持不变。
  - 实时消息只转发给同一对话、同一 Server 连接身份上的 extension 会话。
- **性能**：
  - 没有任何订阅时，ChatHttpClient 不构造事件。
  - 有订阅时，每个事件对每个订阅做一次条件匹配，耗时与订阅数成正比；至少一个订阅匹配时才复制并冻结一次，所有匹配的订阅共用这一份副本。`ConversationTokenUsageStore` 建立四个订阅（5.2 节），流式文字消息不匹配它们；只有这四个订阅时，这些消息不产生副本。
  - 每个 Turn 结束后多一次对话详情请求。Distributed 中断的 Turn 在结算之前，再按 1、2、4、8 秒、之后每 10 秒请求一次，持续时间不超过 `LeaseSeconds`（默认 30 秒）加最后一次请求的间隔。
  - Desktop 中 extension 的每个订阅对应 ChatHttpClient 中的一个订阅，事件在宿主中按条件匹配后才写入端口。
  - extension 每个写入操作的权限检查增加一次查询：按主键定位对话行，在同一条 SQL 中通过 `EXISTS` 使用以 `ProjectConversationId` 开头的现有索引取得是否有已受理 Turn，创建方、`generation` 与 Turn 存在性来自同一语句快照；在开放对话中记录创建方时使用加锁事务。宿主的权限检查直接通过。
  - 详情增加一次是否有 Turn 的查询；列表每一行通过 `EXISTS` 子查询计算开放状态。
- **稳定**：
  - 订阅函数抛出的异常不被捕获，从派发的位置继续抛出（2.5 节）。`ExecutionSession` 在 `handlers.onMessage` 之后才派发 `message` 事件，当前消息已经交给 Chat；Distributed 模式下，后续消息按第 4 节的恢复规则处理。
  - extension 的订阅属于创建它的 frame 页面；端口关闭（frame 关闭、页面被替换、extension 停用）时一起取消。每个 extension 最多同时有 16 个订阅。
- **准确**：
  - 每个 Turn 结束后的详情同步完成时，面板的值等于服务端该对话 `agent_usage` 的累计值，包括 summary、`resultOnly` 与非流式调用的用量。Turn 结束时仍在运行的后台 Agent 任务，其用量在完成之后的下一次详情读取时计入。
  - Turn 运行期间，面板显示已结算用量加上未结算 Turn 已推送的 `UsageContent`；同一条推送消息只计入一次，已经计入基线的 Turn 的补发用量不再计入。
  - Distributed 中断后，持有被中断租约的 worker 退出之前（最长到 `结束时间 + LeaseSeconds`）该 Turn 保持未结算，已推送的用量继续显示，结算之后的同步给出服务端累计值。
  - 刷新页面后，未结算 Turn 已推送的用量从本地恢复，显示值与刷新前相同；补发的消息按 `turnSequence` 去重。
- **兼容**：
  - `manifestVersion` 仍为 1，`chat.observe` 权限与 `api.chat.observe` 是增量。
  - 对话详情增加 `usage.unsettledTurnIds`、`extensionId` 与 `openToExtensions`，列表增加后两个字段；`usage` 的计数在 Turn 结算后计入。中断与结束消息的发送时机不变。
  - 恢复 Agentflow checkpoint 后 Token usage 继续累加（第 5 节）。
- **可观测**：extension 中订阅函数的异常发生在 extension frame 的端口消息处理中，SDK 现有的 `error` 监听以 `log.error` 转发到宿主控制台，带 extensionId 与视图 id；写入被拒绝时以 HubException `4030008` 返回并按现有错误流程记录。

## 总体设计

```mermaid
flowchart LR
  subgraph Hosts["对话宿主"]
    Chat["@agw/chat<br/>Chat、ChatWorkspace<br/>（Web、Desktop）"]
    Native["@agw/chat-native<br/>NativeWorkspaceProvider<br/>（Mobile）"]
    ExtExec["Desktop ExecutionService<br/>（extension execution API）"]
  end
  subgraph Runtime["@agw/chat-runtime"]
    Manager["ExecutionSessionManager"]
    Client["ChatHttpClient<br/>订阅与事件派发"]
    Session["ExecutionSession<br/>SignalR"]
    Usage["ConversationTokenUsageStore"]
  end
  Core["@agw/projects-core<br/>对话 HTTP 函数"]
  Api["AgwApiClient<br/>@agw/api"]
  Server["Agw Server<br/>/api/hubs/exec<br/>/api/projects、/api/agents"]
  Aside["ChatAside<br/>Token usage"]
  Broker["ExtensionBridgeBroker<br/>chat.observe"]
  Frames["extension frame"]
  Access["IProjectConversationAccessFacade<br/>写入检查"]
  Creator["TaskExecutionAppService<br/>记录创建方"]
  Conversations[("project_conversation<br/>extension_id")]

  Chat --> Manager --> Client
  Chat -->|对话 HTTP| Client
  Native --> Client
  ExtExec -->|origin: extension| Client
  Client --> Session -->|SettingCommand.extensionId| Server
  Client --> Core --> Api --> Server
  Client -.->|事件| Usage --> Aside
  Usage -->|Turn 结束后读取详情| Client
  Client -.->|事件与连接身份| Broker -->|chat.event| Frames
  Client -.->|同一对话的 live 消息| ExtExec -->|execution.message| Frames
  Server --> Access
  Server --> Creator --> Conversations
  Access --> Conversations
```

- ChatHttpClient 是创建 `ExecutionSession` 的入口（`ConversationController` 见“待办”），也是 Chat 调用对话 HTTP 接口的入口。
- `ExecutionSession` 把每次 hub 方法调用与每条 `ReceiveMessage` 报告给 ChatHttpClient；对话 HTTP 请求经 ChatHttpClient 包装后的 `AgwApiClient` 发出，再由 projects-core 的函数解析结果。
- 订阅方：Web 与 Desktop 中的 `ConversationTokenUsageStore`；Desktop 中按 extension 的 `chat.observe` 调用建立的订阅。其他组件可以在 `useEffect` 中调用 `chatHttpClient.subscribe(filter, listener)`，返回的函数作为 effect 的清理函数。
- `ExecutionService` 从 broker 连接取得 extension 身份，`ChatHttpClient` 把它绑定到通信事件与执行会话；`SettingCommand` 把身份交给 Server。Server 在解析任务时记录创建方，在每个写入操作之前通过 Projects 的规则检查。
- extension 会话配置成功后订阅同一对话的 live 消息，经 `execution.message` 接收这台 Desktop 上其他会话收到的实时消息。

## 数据库设计

- 表 `project_conversation` 增加列 `extension_id`：
  - 类型：SQLite `TEXT`（模型长度 64），PostgreSQL `character varying(64)`；
  - 不可为空，默认值 `'host'`；`host` 表示宿主，其他值为创建对话的 extension 的 `id`；
  - 在“名词解释”规定的时机写入；清空记录删除依附记录并增加 `generation`，保留创建方；
  - 已有记录填为 `host`。已有对话中，`generation` 为 0 且没有已受理 Turn 的对话满足开放条件。
- 表 `agent_usage` 增加列 `turn_id`：
  - 类型：Guid，可以为 null；
  - 含义：写入这条用量时所在的 Turn，即 `project_conversation_turn.id`；不在 Turn 中执行时为 null；
  - 已有记录为 null，统计时视为已结算。
- 表 `project_conversation_turn` 增加列 `usage_settles_at`：
  - 类型：带时区的时间，可以为 null；
  - 含义：从这个时间起，该 Turn 的执行不再写入用量；Turn 进入终态时写入（5.1 节），Distributed 中断时可能晚于结束时间，worker 退出后提前到退出时间；
  - 迁移时为已经结束的 Turn 写入 `finished_at`，未结束的 Turn 保持 null。
- 表 `durable_execution` 增加列 `interrupted_lease_epoch`：
  - 类型：64 位整数，可以为 null；
  - 含义：中断时仍未过期的那份租约的 `LeaseEpoch`；持有这份租约的 worker 退出并确认结算后清空（5.1 节）；
  - 已有记录为 null。
- 查询先用现有索引 `(project_id, context_id)` 取得对话的用量记录，再按 `turn_id` 分组，关联 Turn 时按主键读取；不新增索引。
- 与仓库中其他关联列相同，不建立外键（`NoForeignKeyModelDiffer`）。
- `ConversationCreators.Host` 定义在 `Agw.Shared/Contracts/Conversations/ConversationCreators.cs`，值为 `"host"`。开放状态使用已有的 `generation` 与 `project_conversation_turn` 记录计算。
- 用量迁移：`Agw.Migrations.Sqlite` 与 `Agw.Migrations.Postgres` 各增加一个迁移，包含 `turn_id`、`usage_settles_at`、`interrupted_lease_epoch` 及 `usage_settles_at` 的数据填充。
- 创建方迁移：两个 provider 各增加一个 `AddConversationCreatorExtension`，只包含 `extension_id`。生成之前确认 `InitialMigrationTests` 的模型基线。
- 迁移都在用户要求时按 `docs/1.Development.md` 中的命令生成。
- SQLite 的 `extension_id` 变更如下，`Down` 按现有 `AddTurnUsageSettlement` 的方式直接删除列：

```sql
-- 增加列。Add the column.
ALTER TABLE "project_conversation" ADD "extension_id" TEXT NOT NULL DEFAULT 'host';
-- 删除列。Remove the column.
ALTER TABLE "project_conversation" DROP COLUMN "extension_id";
```

- PostgreSQL 的 `extension_id` 变更如下：

```sql
-- 增加列。Add the column.
ALTER TABLE project_conversation ADD extension_id character varying(64) NOT NULL DEFAULT 'host';
-- 删除列。Remove the column.
ALTER TABLE project_conversation DROP COLUMN extension_id;
```

## 详细设计

### 1. ChatHttpClient

#### 1.1 位置与实例

- 类 `ChatHttpClient` 位于 `packages/chat-runtime/src/chat-http-client.ts`，从 `@agw/chat-runtime` 入口与新增的子路径 `@agw/chat-runtime/chat-http-client` 导出。`@agw/chat-native` 只使用子路径，与现在使用 `@agw/chat-runtime/execution-session` 的方式相同。
- `@agw/chat-runtime` 的 `package.json` 增加依赖 `@agw/projects-core`。两者都是平台无关、React Native 安全的包，`check-client-boundaries.mjs` 与 `.dependency-cruiser.cjs` 不需要修改。
- 构造参数：

```ts
export type ChatHttpClientOptions = {
  /** 对话 HTTP 接口使用的客户端。The client the conversation HTTP endpoints use. */
  api: Pick<AgwApiClient, "apiGet" | "apiPut" | "apiDelete">;
  /** HTTP 请求发出时的连接身份（1.2 节）。The connection identity when an HTTP request is sent (section 1.2). */
  identity: () => ChatConnectionIdentity;
  /** 创建执行会话时读取的 SignalR 配置。The SignalR configuration read when an execution session is created. */
  execution: () => ExecutionRuntimeConfig;
};
```

- Web 与 Desktop 使用同一文件中的模块单例：

```ts
/** Web 与 Desktop 的实例，使用全局的 API 与执行配置。The Web and Desktop instance, using the global API and execution configuration. */
export const chatHttpClient = new ChatHttpClient({
  api: { apiGet, apiPut, apiDelete },
  identity: () => {
    const { baseUrl, token } = getApiRuntime();
    return { baseUrl, token };
  },
  execution: getExecutionRuntime,
});
```

- `apiGet` 等函数在调用时读取 `configureApiRuntime` 的当前配置，`identity` 读取同一份配置，两者在同一次调用中取值；`getExecutionRuntime` 是 `execution-session.ts` 新增的函数，返回 `configureExecutionRuntime` 的当前配置。Desktop 切换 Server 时不重建实例，与 `executionSessionManager` 相同：已创建的会话保留创建时的配置，之后创建的会话使用新配置。
- `ExecutionSessionManager` 的 `ClientFactory` 增加第二个参数 `key: ExecutionSessionKey`，`attach` 创建会话时传入；默认工厂改为 `(handlers, key) => chatHttpClient.createExecutionSession(key, handlers)`。`ExecutionSessionKey` 的三个字段就是对话的作用范围。
- Mobile 在 `NativeWorkspaceSession` 中为每个已验证的 Server 创建实例（第 7 节）。

#### 1.2 方法

```ts
/** 一个对话的作用范围。The scope of one conversation. */
export type ChatConversationScope = {
  serverId: string;
  projectId: string;
  conversationId: string;
};

/** 对话详情接口的路径模板。The path template of the conversation details endpoint. */
export const CONVERSATION_DETAILS_PATH = "/api/projects/{projectId}/conversations/{conversationId}";

/**
 * 一次通信所用连接的身份：规范化后的 baseUrl（去掉首尾空白与末尾的 `/`，与 `configureApiRuntime` 相同）与 Bearer token。
 * 只交给进程内的订阅函数，不在事件与副本中。
 * The identity of the connection one piece of traffic used: the normalized baseUrl (trimmed and without trailing `/`, as
 * `configureApiRuntime` does) and the Bearer token. Passed only to in-process listeners, never part of the event or its snapshot.
 */
export type ChatConnectionIdentity = { baseUrl: string; token: string | null };

export class ChatHttpClient {
  public constructor(options: ChatHttpClientOptions);

  /**
   * 订阅通信事件，返回取消订阅的函数；条件不合法时抛出。
   * Subscribes to traffic events and returns the unsubscribe function; throws for an invalid filter.
   */
  public subscribe(
    filter: ChatEventFilter,
    listener: (event: ChatEvent<AiMessage>, connection: ChatConnectionIdentity) => void,
  ): () => void;

  /** 当前 HTTP 连接身份，即规范化后的 `options.identity()`。The current HTTP connection identity: the normalized `options.identity()`. */
  public getConnectionIdentity(): ChatConnectionIdentity;

  /** 创建一个对话的执行会话（1.3 节）。Creates the execution session of one conversation (section 1.3). */
  public createExecutionSession(
    scope: ChatConversationScope,
    handlers: ExecutionHubHandlers,
    options?: { runtime?: ExecutionRuntimeConfig; origin?: ChatEventOrigin },
  ): ExecutionSession;

  public getConversation(
    scope: ChatConversationScope,
    signal?: AbortSignal,
    origin?: ChatEventOrigin,
  ): Promise<ConversationDetails>;
  public getConversationMessages(
    scope: ChatConversationScope,
    options: ConversationMessagePageOptions,
    origin?: ChatEventOrigin,
  ): Promise<ConversationMessagePage>;
  public getConversationHistory(
    scope: ChatConversationScope,
    signal?: AbortSignal,
    origin?: ChatEventOrigin,
  ): Promise<ConversationHistory>;
  public getConversationTurnInputs(
    scope: ChatConversationScope,
    signal?: AbortSignal,
    origin?: ChatEventOrigin,
  ): Promise<ConversationTurnInputSummary[]>;
  public clearConversationRecords(
    scope: ChatConversationScope,
    origin?: ChatEventOrigin,
  ): Promise<boolean>;
  public getPermissionCapabilities(
    scope: ChatScope,
    target: { type: "agent" | "agentflow"; id: string },
    signal?: AbortSignal,
    origin?: ChatEventOrigin,
  ): Promise<ApiResponse<"/api/agents/permission-capabilities", "get">>;
  public getAgentSuggestions(
    scope: ChatScope,
    query: { projectId?: string; agentId: string },
    signal?: AbortSignal,
    origin?: ChatEventOrigin,
  ): Promise<AgentSuggestionsResponse>;
}
```

`ConversationDetails`、`ConversationMessagePage`、`ConversationMessagePageOptions`、`ConversationHistory`、`ConversationTurnInputSummary` 来自 `@agw/projects-core`，`ApiResponse` 来自 `@agw/api`，`AgentSuggestionsResponse` 来自 `@agw/chat-core`。

HTTP 方法的最后一个参数 `origin` 默认使用 `CLIENT_ORIGIN`（`{ kind: "client" }`）。宿主代 extension 发出请求时传入 `{ kind: "extension", extensionId }`，同一次请求的 request、response 与 history 消息事件使用这个发起方；`getConversationHistory` 把它传给每次详情与分页调用。

订阅函数的第二个参数是事件的连接身份：HTTP 事件为请求发出时 `options.identity()` 的值，同一个请求的 request、response 与 history 消息事件使用同一个值；执行通道事件为创建会话时 `runtime` 的 `baseUrl` 与 `token`（1.3 节）。两种来源的 `baseUrl` 都由 ChatHttpClient 做同样的规范化。Desktop 的 broker 用它判断事件是否属于当前连接（8.4 节），`ConversationTokenUsageStore` 用它判断同步请求能否发出（5.3 节第 9 条）。

各方法对应的接口：

- `getConversation`：`GET /api/projects/{projectId}/conversations/{conversationId}`，调用 `getProjectConversationDetails`。
- `getConversationMessages`：`GET /api/projects/{projectId}/conversations/{conversationId}/messages`，调用 `getProjectConversationMessages`；response 事件之后，为 `items` 中的每条消息按顺序派发 `source` 为 `history` 的 `message` 事件。
- `getConversationHistory`：先调用 `getConversation`，再以 `direction: "newer"`、`pageSize: 100` 循环调用 `getConversationMessages`，直到 `hasMore` 为 false 或没有 `nextCursor`；返回值与现在的 `getProjectConversationHistory` 相同，每一页都通过 `getConversationMessages` 产生事件。
- `getConversationTurnInputs`：分页请求 `GET /api/projects/conversation-turns`，调用 projects-core 的 `getConversationTurnInputs`。该函数增加第三个参数 `client: ProjectConversationApiClient = browserClient`，与同一文件中其他函数的写法相同。
- `clearConversationRecords`：`DELETE /api/projects/{projectId}/conversations/{conversationId}/clear-records`，调用 `clearProjectConversationRecords`。
- `getPermissionCapabilities`：`GET /api/agents/permission-capabilities`，query 为 `{ type, id }`，Agent 的 `type` 为 0，Agentflow 为 1。
- `getAgentSuggestions`：`GET /api/agents/suggestions`，query 为调用方传入的 `getAgentSuggestionQueryParams` 结果。

对话列表的读取、重命名、删除、清除项目全部对话，以及会话活动状态快照（`getConversationActivity`）由 `@agw/projects-core` 直接请求。

改动后没有调用方、一并删除的代码：

- projects-core 的 `getProjectConversationHistory` 与 `ProjectConversationService.getProjectConversationHistory`：唯一的调用方 Mobile 改用 `getConversationHistory`（第 7 节）。`projects-core.test.ts` 中对应的用例移到 `chat-http-client.test.ts`。
- projects-core 的 `ConversationDetails.usage` 与 `ProjectConversationResponse.usage`：唯一的使用方是 `ChatWorkspace` 写入 `sessionSeed.usage`（第 6 节删除），基线改由 `ConversationTokenUsageStore` 从 response 事件读取。`task-client.ts` 中因此不再使用的 `normalizeTokenUsage`、`TokenUsage`、`TokenUsageInput` 导入一并删除。

#### 1.3 执行会话

- `createExecutionSession` 返回 `new ExecutionSession(handlers, runtime, observer, extensionId)`：
  - `runtime` 为 `options.runtime`，省略时为 `this.options.execution()`；
  - `observer` 绑定 `scope`、`origin`（省略时为 `{ kind: "client" }`）与连接身份，把 `ExecutionSession` 报告的 hub 调用与消息转换为 `execution` 通道的事件（第 4 节）；
  - 连接身份为 `{ baseUrl: 规范化后的 runtime.baseUrl, token: runtime.token }`，在会话的整个生命周期中不变，与会话实际使用的配置相同。
  - `extensionId` 为 `origin.kind === "extension" ? origin.extensionId : null`，执行会话身份与通信事件发起方来自同一个选项。
- 调用方：
  - `ExecutionSessionManager` 的默认工厂（1.1 节）；
  - Mobile 的 `NativeExecutionSession`（第 7 节）；
  - Desktop 的 `ExecutionService`：`runtime` 为连接标识对应的配置，`origin` 为 `{ kind: "extension", extensionId }`（8.5 节）。

#### 1.4 HTTP 请求

- 每个 HTTP 方法通过 `createApi(scope, origin)` 为本次调用创建包装后的客户端，形状为 projects-core 的 `ProjectConversationApiClient`（`apiGet`、`apiPut`、`apiDelete`），交给 projects-core 的函数，或在 `getPermissionCapabilities`、`getAgentSuggestions` 中直接调用。包装的每个函数依次完成：
  1. 派发 `request` 事件：`method` 为大写的 HTTP 方法，`path` 为调用时传入的 OpenAPI 路径模板，`params` 为调用时的 `params`（没有时为 null），`body` 为请求体（没有时为 null）；
  2. 调用 `options.api` 中的同名函数；
  3. 成功时派发 `ok: true` 的 `response` 事件，`value` 为解包后的返回值（没有返回值时为 null），然后返回该值；失败时派发 `ok: false` 的 `response` 事件，然后抛出原异常；
  4. 把本次 request 事件的 `id` 记录在包装上，`getConversationMessages` 用它作为历史消息事件的 `requestId`。
- 错误转换为 `ChatEventError`：
  - `ApiError`：`status` 为 HTTP 状态码，`body` 为解析后的响应体；
  - 其他异常（`ApiTransportError`、`AbortError` 等）：`status` 与 `body` 为 null；
  - `name`、`message` 取自异常。
- HTTP 事件使用本次调用的 `origin`，历史分页的消息事件沿用同一个值。

### 2. 通信事件

#### 2.1 类型

类型定义在 `packages/execution-core/src/chat-events.ts`，从 `@agw/execution-core` 入口导出。`@agw/extension-sdk` 只依赖 `@agw/execution-core`，Extension API 直接使用这些类型（8.2 节）。与 `message.ts` 相同，类型对消息类型泛型化，`@agw/chat-runtime` 使用 `ChatEvent<AiMessage>`。

```ts
import type { ExecutionMessage } from "./types";

/**
 * 事件所属的 Server、Project 与对话；不属于对话的请求中为 null。
 * The server, project and conversation of an event; null for requests outside a conversation.
 */
export type ChatScope = {
  serverId: string;
  projectId: string | null;
  conversationId: string | null;
};

/** 发起通信的一方。Who started the traffic. */
export type ChatEventOrigin = { kind: "client" } | { kind: "extension"; extensionId: string };

export type ChatChannel = "http" | "execution";

/** ExecutionSession 调用的 hub 方法。The hub methods ExecutionSession calls. */
export type ExecutionHubMethod =
  | "DispatchCommand"
  | "GetExecutionProvider"
  | "GetAgentflowCheckpoints"
  | "FindInProcessExecution"
  | "RecoverInProcessExecution";

export type ChatHttpRequest = {
  method: "GET" | "POST" | "PUT" | "DELETE";
  /** OpenAPI 路径模板。The OpenAPI path template, e.g. `/api/projects/{projectId}/conversations/{conversationId}`. */
  path: string;
  params: { path?: Record<string, unknown>; query?: Record<string, unknown> } | null;
  body: unknown;
};

export type ChatExecutionRequest = {
  method: ExecutionHubMethod;
  /** hub 方法参数；DispatchCommand 的第一个参数是命令对象。The hub method arguments; DispatchCommand's first one is the command. */
  args: readonly unknown[];
};

type ChatRequestPart =
  | { channel: "http"; request: ChatHttpRequest }
  | { channel: "execution"; request: ChatExecutionRequest };

export type ChatEventError = {
  name: string;
  message: string;
  /** HTTP 状态码；hub 调用与网络错误为 null。The HTTP status; null for hub calls and network failures. */
  status: number | null;
  /** 服务端返回的响应体；没有时为 null。The body the server returned; null when none. */
  body: unknown;
};

type ChatEventBase = {
  id: string;
  /** RFC 3339 UTC 时间。RFC 3339 UTC time. */
  time: string;
  scope: ChatScope;
  origin: ChatEventOrigin;
};

export type ChatRequestEvent = ChatEventBase & { kind: "request" } & ChatRequestPart;

export type ChatResponseEvent = ChatEventBase & {
  kind: "response";
  /** 对应的 request 事件 id。The id of the matching request event. */
  requestId: string;
} & ChatRequestPart &
  ({ ok: true; value: unknown } | { ok: false; error: ChatEventError });

/**
 * 消息来源：live 是 ReceiveMessage 推送的消息，经 execution 通道到达；history 是历史分页响应中的消息，经 http 通道到达，
 * requestId 为该页 request 事件的 id。
 * The message source: live is a message pushed by ReceiveMessage over the execution channel; history is a message of a
 * history page response over the http channel, and requestId is that page's request event id.
 */
type ChatMessageSourcePart =
  | { source: "live"; channel: "execution"; requestId: null }
  | { source: "history"; channel: "http"; requestId: string };

export type ChatMessageSource = ChatMessageSourcePart["source"];

export type ChatMessageEvent<TMessage extends ExecutionMessage = ExecutionMessage> =
  ChatEventBase & { kind: "message"; message: TMessage } & ChatMessageSourcePart;

export type ChatEvent<TMessage extends ExecutionMessage = ExecutionMessage> =
  ChatRequestEvent | ChatResponseEvent | ChatMessageEvent<TMessage>;

export type ChatEventKind = ChatEvent["kind"];
```

- response 事件带有同一个请求，订阅方不需要按 `requestId` 自己关联请求。
- live 消息是服务端发送的原始消息，没有客户端 `scopeStreamingMessage` 设置的顶层 `streamingScopeId`。`ReceiveMessage` 推送的消息都是 live，包括断线重连或刷新页面后服务端按 cursor 补发的消息。
- history 消息只来自分页响应的 `items`，每页每条消息一次。`turns` 中的输入与 Result 是页中各个 Turn 的锚点，可能与 `items` 或其他页的内容重复，只出现在 response 事件的 `value` 中。
- 同一条消息先作为 live 推送、之后又出现在历史分页中时，两次各有一个事件，`source` 分别为 `live` 与 `history`。订阅推送消息用 `sources: ["live"]`，订阅历史消息用 `sources: ["history"]`（第 3 节）。

#### 2.2 标识与时间

- `id` 由 ChatHttpClient 用 `@agw/api` 的 `createUuidV7()` 生成；response 事件的 `requestId` 是对应 request 事件的 `id`。
- `time` 为派发事件时的 `new Date().toISOString()`。

#### 2.3 顺序

- 每个 ChatHttpClient 按产生的顺序同步派发事件。
- request 事件在请求发出之前派发；response 事件在结果返回之后、方法返回调用方之前派发；history 消息事件在该页 response 事件之后按 `items` 顺序派发。
- live 消息事件在 `handlers.onMessage` 处理该消息之后派发（第 4 节）。

#### 2.4 副本

- 派发时先用原对象做条件匹配，没有匹配的订阅时直接返回。
- 有匹配时，对整个事件做一次 `JSON.parse(JSON.stringify(event))` 得到副本，递归 `Object.freeze` 后交给每个匹配的订阅。这一步由 `@agw/execution-core` 导出的 `snapshotChatEvent(event)` 完成；broker 去掉环境变量时（8.4 节）也用它生成新的副本。
- 连接身份不在事件中，派发时作为订阅函数的第二个参数传入，不进入副本。
- 事件内容都是线上传输的 JSON 数据：SignalR 使用 JSON hub 协议，对话 HTTP 接口的请求与响应都是 JSON，JSON 副本就是线上的表示，在 Web、Desktop、Mobile 中的结果相同。值为 `undefined` 的字段在副本中不存在，因此 `value`、`body`、`params` 没有内容时写为 null。

#### 2.5 派发与异常

- 派发开始时确定匹配的订阅列表。派发过程中被取消的订阅不再收到当前事件；派发过程中新增的订阅从下一个事件开始。
- 订阅函数不应抛出异常。ChatHttpClient 不捕获订阅函数的异常，异常从派发的位置继续抛出：
  - request 事件中抛出：请求不发出，方法以该异常拒绝；
  - response 事件或历史消息事件中抛出：方法以该异常拒绝；
  - hub 调用的 request、response 事件中抛出：该 hub 调用以该异常拒绝，按 `ExecutionSession` 现有的错误流程处理；
  - `ReceiveMessage` 的 message 事件中抛出：异常在 SignalR 的回调中抛出，由 SignalR 记录为 error 日志；`handlers.onMessage` 已经处理过这条消息。Distributed 模式保留最后连续处理成功的 cursor，暂停后续 Turn 消息的处理，直到失败位置的消息补发并处理成功（第 4 节）。

### 3. 订阅条件

```ts
export type ChatEventFilter = {
  kinds?: readonly ChatEventKind[];
  channels?: readonly ChatChannel[];
  origins?: readonly ChatEventOrigin["kind"][];
  serverId?: string;
  projectId?: string;
  conversationId?: string;
  /** 以下四项只描述消息。The four fields below describe messages only. */
  sources?: readonly ChatMessageSource[];
  roles?: readonly string[];
  messageTypes?: readonly (string | null)[];
  contentTypes?: readonly string[];
};

/** 条件合法时返回 null，否则返回问题描述。Returns null for a valid filter, otherwise a description of the problem. */
export function validateChatEventFilter(value: unknown): string | null;

export function matchesChatEvent(filter: ChatEventFilter, event: ChatEvent): boolean;
```

匹配规则：

- 省略的字段不限制；给出的字段全部满足才匹配；数组字段中任一值满足即可。
- `kinds`、`channels`、`origins` 分别匹配事件的 `kind`、`channel`、`origin.kind`。
- `sources` 匹配 message 事件的 `source`：`["live"]` 只匹配推送消息，`["history"]` 只匹配历史消息。
- `serverId`、`projectId`、`conversationId` 与 `scope` 中同名字段相等时满足；`scope` 中为 null 的字段不满足。
- `roles` 与 `message.role` 按忽略大小写的相等规则匹配，与服务端 `AiRole` 相同；没有 `role` 的消息不满足。
- `messageTypes` 与 `message.additionalProperties?.type` 匹配；`null` 表示没有字符串类型 `type` 的消息。
- `contentTypes`：消息中至少有一个 content 的 `type` 在列表中。
- 给出 `sources`、`roles`、`messageTypes`、`contentTypes` 中任一项时，request 与 response 事件不满足。
- 空条件 `{}` 匹配全部事件。

校验规则（`validateChatEventFilter`）：

- 值必须是普通对象，只能有上面列出的字段。
- 数组不能为空；`kinds`、`channels`、`origins`、`sources` 的元素必须是已定义的值。
- `roles`、`contentTypes` 的元素以及 `serverId`、`projectId`、`conversationId` 必须是非空字符串；`messageTypes` 的元素是非空字符串或 null。
- `ChatHttpClient.subscribe` 遇到问题时抛出 `Error`，信息为问题描述；broker 返回 `ERR_INVALID_ARGUMENT`（8.4 节）。

常用条件：

- 全量订阅：`{}`。
- 一个对话的推送消息：`{ sources: ["live"], conversationId }`。
- 一个对话的历史消息：`{ sources: ["history"], conversationId }`。
- 运行中推送的 Result：`{ sources: ["live"], messageTypes: ["result"] }`；历史中的 Result：`{ sources: ["history"], messageTypes: ["result"] }`。
- Turn 开始与结束：`{ kinds: ["message"], messageTypes: ["agw-turn-start", "agw-turn-finished"] }`。
- 工具调用与结果：`{ contentTypes: ["FunctionCallContent", "FunctionResultContent"] }`。
- assistant 的输出：`{ roles: ["assistant"] }`。
- 发出的执行命令：`{ kinds: ["request"], channels: ["execution"] }`，再按 `request.method === "DispatchCommand"` 与 `request.args[0].type`（如 `ExecCommand`、`InterruptCommand`）区分。
- 用量：`{ sources: ["live"], contentTypes: ["UsageContent"] }`。

### 4. ExecutionSession 的观察点

```ts
/** ExecutionSession 向 ChatHttpClient 报告通信。How ExecutionSession reports traffic to ChatHttpClient. */
export type ExecutionTrafficObserver = {
  /**
   * hub 调用发出前调用；返回的函数在调用结束时接收结果。
   * Called before a hub call; the returned function receives its outcome.
   */
  request(
    method: ExecutionHubMethod,
    args: readonly unknown[],
  ): (outcome: { ok: true; value: unknown } | { ok: false; error: unknown }) => void;
  /** handlers.onMessage 处理一条 ReceiveMessage 之后调用。Called after handlers.onMessage handles a ReceiveMessage. */
  message(message: AiMessage): void;
};
```

- `ExecutionSession` 的构造函数增加第三个参数 `observer: ExecutionTrafficObserver | null = null` 与第四个参数 `extensionId: string | null = null`。
- `packages/execution-core/src/protocol.ts` 的 `ExecutionSettingCommandInput` 增加 `extensionId?: string`，`buildSettingCommand` 在有值时写入命令。
- `ExecutionSession` 使用私有方法生成 `SettingCommand`：`buildSettingCommand({ ...this.setting, extensionId: this.extensionId ?? undefined })`。`configure` 与 `restoreAfterReconnect` 都使用它，每次配置与重连携带同一个身份。宿主会话省略该字段，调用方在设置中多传的属性会被覆盖。公开的 `ExecutionSetting` 类型与导出的 `buildSettingCommand(setting)` 保持现有形状。
- 新增私有方法 `invoke<T>(method: ExecutionHubMethod, ...args: unknown[]): Promise<T>`：先调用 `this.observer?.request(method, args)`，再调用 `this.connection.invoke`；成功时以 `{ ok: true, value }` 调用返回的函数后返回 `value`，失败时以 `{ ok: false, error }` 调用后抛出原异常。现有的 7 处 `this.connection.invoke` 全部改为 `this.invoke`，分别在 `dispatch`、`listAgentflowCheckpoints`、`restoreAfterReconnect`、`discoverInProcessExecution`、`recoverInProcessExecution`、`restoreDurableSubscription`、`refreshExecutionProvider` 中。
- `ReceiveMessage` 回调在 `this.handlers.onMessage(scopedMessage)` 之后调用 `this.observer?.message(message)`，传入服务端原始消息；与 `handlers.onMessage` 相同，会话 dispose 之后不再报告。
- Distributed 模式下，恢复进度按以下规则更新：

  1. `streamCursor` 与 attachment store 中的 cursor 都表示当前 Turn 最后连续处理成功的位置。开始或恢复 Turn 时建立 `activeTurnId` 与初始 cursor；`ReceiveMessage` 提取的 `turnSequence` 只作为待提交位置。`this.handlers.onMessage(scopedMessage)` 与 `this.observer?.message(message)` 都正常返回后，才调用 `updateDurableProgress` 更新内存 cursor 并写入 attachment store。`observer` 为 null 时，处理成功以 `handlers.onMessage` 正常返回为准。
  2. 处理带有 `turnId`、`turnSequence` 的消息之前，记录待完成的位置；两个回调都成功后清除。任一回调抛出异常时，这个位置保留，异常继续抛出，内存与存储中的 cursor 保持不变。存在待完成位置时，只重新处理相同 `turnId`、`turnSequence` 的消息；其他带有 Turn 位置的消息不交给 `handlers.onMessage` 或 observer，也不推进 cursor，等待恢复订阅后由服务端补发。因此序号 N 失败后，收到 N+1 不会越过 N，订阅方记录的最大序号也不会提前增加。
  3. `restoreAfterReconnect` 与手动重连使用内存中的已确认 cursor；刷新页面后的 `configure` 使用 attachment store 中的已确认 cursor。重新订阅不会清除待完成位置；失败位置处理成功后，才继续处理其后的消息。`reconcileStartFailure` 从空 cursor 订阅时同样遵守这条规则。存在待完成位置时，`hasRunningTurn()` 保持 true，`execute` 与 `resumeCheckpoint` 不能覆盖当前 Turn 的恢复状态。
  4. 带有 Turn 位置的结束消息也必须完成两个回调，之后才调用 `finishActiveTurn` 清除 attachment、重置执行状态并完成等待结束的 Promise。结束消息处理失败时保留当前 Turn 与 cursor；无 Turn 位置的通知不能清除这份待恢复状态。`ExecutionSessionManager` 对每次收到的结束消息都在当前派发结束后检查发送队列，包括补发的结束消息；检查时仍由 `hasRunningTurn()` 阻止未处理成功的 Turn 继续发送。服务端明确返回 `DurableExecutionNotFound` 时，沿用现有的 attachment 清理流程。
  5. 订阅方（例如 `ConversationTokenUsageStore`）先保存自己的状态，`ExecutionSession` 再提交 cursor；页面在两次写入之间关闭时，这条消息在恢复后被补发，已经处理过它的订阅方按 `turnSequence` 去重。消息派发中途失败时，已经成功处理的订阅方在补发时同样可能再次收到该消息。

- 执行通道的事件只来自 hub 方法调用与 `ReceiveMessage`。`connection.start`、`connection.stop` 与自动重连本身没有事件；重连后恢复设置与订阅时发出的 `SettingCommand`、`SubscribeTurnCommand` 产生事件。
- `recoverInProcessExecution(interrupt)` 在 `interrupt` 为 true 且 `isServerRejection` 确认 Server 拒绝时，重新安排每秒一次的状态查询后抛出异常。只读会话停止宿主 Turn 被拒绝后，原 Turn 继续运行；状态查询负责在它结束后清除会话的忙碌状态，因此拒绝停止之后仍需持续查询。
- ChatHttpClient 的 observer 把 `request` 转换为 `execution` 通道的 request 与 response 事件：`request` 为 `{ method, args }`；hub 方法返回 `undefined` 时 `value` 为 null；hub 错误的 `status`、`body` 为 null。`message` 转换为 `source` 为 `live`、`channel` 为 `execution`、`requestId` 为 null 的 message 事件。

```mermaid
sequenceDiagram
  participant M as ExecutionSessionManager
  participant S as ExecutionSession
  participant C as ChatHttpClient
  participant Hub as /api/hubs/exec
  M->>S: execute(request)
  S->>C: observer.request("DispatchCommand", [ExecCommand])
  C->>C: 派发 request 事件
  S->>Hub: invoke DispatchCommand
  Hub-->>S: 完成
  S->>C: 结果 { ok: true }
  C->>C: 派发 response 事件
  Hub-->>S: ReceiveMessage(message)
  S->>M: handlers.onMessage(scopedMessage)
  S->>C: observer.message(message)
  C->>C: 派发 message 事件
  C-->>S: 派发成功
  S->>S: Distributed 提交 cursor；结束消息清除 attachment
```

### 5. Token usage

#### 5.1 服务端：用量所属的 Turn 与结算时间

- `AgentUsage` 增加 `TurnId`（`Guid?`，列 `turn_id`）；`ProjectConversationTurn` 增加 `UsageSettlesAt`（`DateTimeOffset?`，列 `usage_settles_at`），表示从这个时间起该 Turn 的执行不再写入用量（见“数据库设计”）。
- 写入用量：`IAgentUsageRecorder.AddAsync` 增加参数 `Guid? turnId`，`AgentUsageRecorder` 把它写入 `TurnId`。`AgentTelemetryMiddleware.RecordAsync` 与 `AgentTurnSummaryService.RecordUsageAsync` 传入 `ExecutionScope.Current?.Context.TurnId`，不在 Turn 中执行时为 null。
- 写入结算时间：
  - `IConversationTurnStore.FinishAsync` 增加参数 `DateTimeOffset usageSettlesAt`，与终态一起写入。
  - 以下位置写入终态时，执行已经退出或没有开始，传入结束时间：InProcess 模式的 `TurnPipeline`；worker 提交终态结果的 `CommitResultAsync`；`DurableExecutionCoordinator` 与 `TurnAcceptanceService` 中启动或受理失败时写入的 `Failed`；进程重启后 `InProcessTurnRecovery` 写入的 `Interrupted`。
  - Distributed 中断（`DurableExecutionCoordinator.InterruptAsync`）先提交终态与结束事件，执行之后才退出：本实例上的执行由 `CancellationTokenSource.CancelAsync` 取消，它不等待执行退出；其他实例上的 worker 要到下一次续租失败才停止。因此 `DurableExecutionStore.InterruptAsync` 在清空 `WorkerId` 与 `LeaseExpiresAt` 之前先读出它们：中断前有未过期的租约时，传入 `结束时间 + LeaseSeconds`，并把这份租约的代次（`LeaseEpoch`）写入新增的列 `durable_execution.interrupted_lease_epoch`；没有租约时（排队中、等待人工输入）传入结束时间，该列保持 null。
  - worker 的 `RunLeasedSegmentAsync` 在 `ExecuteSegmentAsync` 返回或抛出之后（Agent 的流已经释放，telemetry 的 `finally` 已经写入用量），在锁定 `durable_execution` 行的事务中（`IDurableExecutionLeases.RunLockedAsync`）确认结算：只有 `interrupted_lease_epoch` 等于自己租约的 `Epoch` 时，才清空该列，并调用新增的 `IConversationTurnStore.SettleUsageAsync(turnId)`；`SettleUsageAsync` 在 Turn 已是终态且 `UsageSettlesAt` 晚于当前时间时改为当前时间，其他情况不修改。
  - `TryClaimAsync` 每次领取租约都把 `LeaseEpoch` 加一，`InterruptAsync` 不修改它，所以一个代次只对应一份租约。租约过期后由另一实例接管、接管者又被中断时，先退出的旧 worker 代次不同，不能结算；只有被中断的那份租约的 worker 退出时才提前结算。
  - 中断后，持有被中断租约的 worker 最迟在一个 `LeaseRenewSeconds`（默认 10 秒）之后续租失败并停止，结算时间通常由它提前写入；它所在的实例已经停止时，到 `结束时间 + LeaseSeconds`（默认 30 秒）视为结算。
- `ProjectConversationAppService.GetUsageAsync` 依次执行两次查询：
  1. 按 `turn_id` 分组汇总该对话 `contextId` 下的用量，同一条查询以 `LEFT JOIN` 关联 `project_conversation_turn`，得到每组所属 Turn 的状态与 `usage_settles_at`，保留没有对应 Turn 行的用量分组；
  2. 读取该对话中未结算的 Turn id。

  未结算 Turn 指状态为 `Accepted`、`Running` 的 Turn，以及终态但 `usage_settles_at` 晚于当前时间的 Turn。结果取两部分的并集：第 2 步的结果，以及第 1 步中所属 Turn 未结算的分组。`usage` 为其余分组之和，`usage.unsettledTurnIds` 为未结算 Turn 的 id。`turn_id` 为 null，或第 1 步没有关联到 Turn 行的分组，统计时按已结算计入；即使 `turn_id` 非空，也保留该分组，不将它的 id 加入 `unsettledTurnIds`。清空记录与 checkpoint 恢复删除 Turn 时保留 `agent_usage` 及其原有 `turn_id`，这些用量继续计入对话累计值。

  第 1 步已读为未结算的 Turn 在两次查询之间结算或删除时，本次仍按该状态加入并集；删除后的下一次详情读取按没有对应 Turn 行的分组计入。两次查询之间开始的 Turn 没有被汇总。这些情况下本次 `usage` 都不含相应 Turn 的用量，客户端继续计入它们的推送用量（5.3 节）。

- `ProjectConversationUsage` 增加 `UnsettledTurnIds`（`IReadOnlyList<Guid>`），对话详情的 `usage` 随之带上它。
- 统计边界成立的依据：
  - Agent 的每次运行在流结束时写入用量（`AgentTelemetryMiddleware` 的 `finally`），summary 在返回 Result 之前写入（`AgentTurnSummaryService.CreateResultAsync`）；
  - 除 Distributed 中断以外，终态都在执行退出之后写入：`TurnPipeline` 在消息流结束之后写入终态（`turn.FinishAsync`），再发出 `agw-turn-finished`；`CommitResultAsync` 在 Segment 执行返回之后提交终态与结束事件；
  - Distributed 中断时，从提交终态到执行退出的这段时间由结算时间覆盖。

  对于查询时仍有关联 Turn 行的分组，一个 Turn 不在 `unsettledTurnIds` 中时，`usage` 已经包含它在结算之前写入的全部用量，包括 summary、`resultOnly` 时没有推送的用量和非流式调用（例如后台 Agent 的运行）的用量。没有对应 Turn 行的分组按查询时已保存的用量累计，后续写入的用量在下一次详情读取时计入。

- 用 `background_agents_start_task` 启动、在 Turn 结算时仍未完成的后台任务，在完成时写入用量。后台 Agent 的作用域由 `ExecutionScope.CreateBackgroundAgentScope` 从启动它的作用域派生，`TurnId` 不变，这条用量记在已经结算的 Turn 上，此后的详情响应都会计入。后台 Agent 没有交互通道，它的用量不经过推送，不会重复计入。worker 在结算时间之后才写入的用量（例如实例长时间停顿，或退出耗时超过 `LeaseSeconds`），同样在下一次读取详情时计入。

#### 5.2 客户端：`ConversationTokenUsageStore`

`packages/chat-runtime/src/conversation-token-usage.ts`：

```ts
export class ConversationTokenUsageStore {
  /**
   * 构造时订阅 client；storage 保存未结算 Turn 的推送用量，为 null 时只保存在内存中。
   * Subscribes to the client on construction; storage keeps the pushed usage of unsettled turns, which stays in memory only when it is null.
   */
  public constructor(
    client: Pick<ChatHttpClient, "subscribe" | "getConversation" | "getConnectionIdentity">,
    storage: ExecutionAttachmentStore | null,
  );
  /** 对话的用量；没有记录时为 EMPTY_TOKEN_USAGE。The conversation's usage; EMPTY_TOKEN_USAGE without a record. */
  public get(key: ExecutionSessionKey): TokenUsage;
  public subscribe(listener: () => void): () => void;
  /** 取消对 client 的订阅与已经安排的同步。Unsubscribes from the client and cancels scheduled syncs. */
  public dispose(): void;
}

/** Web 与 Desktop 使用的实例。The instance Web and Desktop use. */
export const conversationTokenUsage = new ConversationTokenUsageStore(
  chatHttpClient,
  globalThis.localStorage ?? null,
);
```

- `ExecutionAttachmentStore` 是 `execution-session.ts` 中 durable attachment 使用的存储接口（`getItem`、`setItem`、`removeItem`）。读写失败时与 durable attachment 的处理相同：统计照常进行，只失去刷新页面后的恢复。
- `packages/chat-runtime/src/use-conversation-token-usage.ts`：`useConversationTokenUsage(key: ExecutionSessionKey | null, store = conversationTokenUsage): TokenUsage`，用 `useSyncExternalStore` 读取，`key` 为 null 时返回 `EMPTY_TOKEN_USAGE`。两者从 `@agw/chat-runtime` 入口导出。

store 建立四个订阅：

- `{ kinds: ["request", "response"], channels: ["http"] }`：对话详情的请求与响应；
- `{ kinds: ["response"], channels: ["execution"] }`：hub 调用的结果，用于第 6 条规则；
- `{ sources: ["live"], contentTypes: ["UsageContent"] }`：推送的用量；
- `{ sources: ["live"], messageTypes: ["agw-turn-start", "agw-turn-finished"] }`：Turn 的开始与结束。

#### 5.3 统计规则

每个对话（按 `getExecutionSessionKey` 计算的键）记录：

- 基线：被采用的详情响应中的用量与 `unsettledTurnIds`，以及该响应对应请求的序号；
- 每个 Turn 的推送用量：`UsageContent` 合计、已经计入的最大 `turnSequence`、开始消息到达时的序号、是否收到结束消息；
- 该对话最近一个执行通道事件或被采用的详情响应的连接身份，第 9 条规则用它判断能否发出同步请求；
- 该对话最近一次确认没有运行中执行的序号，第 6 条规则使用。

序号是 store 为收到的每个事件依次分配的整数。ChatHttpClient 按产生顺序同步派发事件，序号的大小就是事件的先后。

规则：

1. 对话详情的 request 事件（`GET`，`CONVERSATION_DETAILS_PATH`）：记下请求序号，response 事件按 `requestId` 找到它。
2. 对话详情的 response 事件：`ok` 为 true 且请求序号大于当前基线的请求序号时采用，键使用 `scope.serverId` 与响应中的 `projectId`、`conversationId`（与 `ChatWorkspace` 随后传给 `Chat` 的 `details.conversationId` 相同）。失败的响应与请求序号更小的响应不改变用量。
3. live 的 `agw-turn-start`：记下该 Turn 开始消息到达时的序号。
4. live 的 `UsageContent`：`turnSequence` 不大于该 Turn 已经计入的最大序号时忽略，这样补发与重复投递的消息不会再次计入；否则加到该 Turn 的推送用量，并更新最大序号。服务端 `TurnBroadcast.Stamp` 给 Turn 内每条消息加上 `turnId` 与 `turnSequence`，并按序号顺序推送与补发，所以只记最大序号就能去重；缺少这两个字段时抛出异常。
5. live 的 `agw-turn-finished`：记下该 Turn 已结束，当前事件派发结束后（`queueMicrotask`）发出同步请求（第 9 条）。
6. 执行通道的 response 事件表明对话已经没有运行中的执行（`FindInProcessExecution` 返回 null，或 `RecoverInProcessExecution` 返回 false）时，记下这次确认的序号。同时满足以下两点时发出同步请求：
   - 该对话没有基线，或基线的请求早于这次确认；
   - 基线的 `unsettledTurnIds` 不为空，或 store 中有该对话未结算 Turn 的记录（推送用量可以为零）。

   收到确认时与采用新基线时各检查一次，确认先到、基线后到时同样会同步；同步得到的基线请求晚于确认，不会再次触发。InProcess 模式下刷新页面后，新的连接收不到原执行的结束消息，由这条规则结束统计；开启 `resultOnly`，或在第一条 `UsageContent` 到达之前刷新时，store 中没有推送用量，基线中的未结算 Turn 同样触发同步。

7. 显示值为基线加上满足以下任一条件的 Turn 的推送用量：Turn 在基线的 `unsettledTurnIds` 中；Turn 的开始消息晚于基线的请求到达。没有基线的对话（新对话）计入全部 Turn 的推送用量。
8. 采用新基线后：
   - 删除不满足第 7 条的 Turn 的记录，它们的用量已经包含在基线中；
   - 仍满足第 7 条、已经收到结束消息、并且列在 `unsettledTurnIds` 中的 Turn（Distributed 中断后 worker 尚未退出），在 1、2、4、8 秒后各发一次同步请求，之后每 10 秒一次，直到该对话没有这样的 Turn。服务端最迟在 `结束时间 + LeaseSeconds` 结算（5.1 节），所以这些请求的次数有上限。某次同步请求失败时停止，等待下一次读取详情。
9. 同步请求：以触发它的事件的作用范围调用 `getConversation(scope)`，并且只在该对话记下的连接身份等于 `getConnectionIdentity()` 时发出。身份检查与发出请求在同一次同步调用中完成，中间不会改变。两者不同时（例如 Desktop 已切换到另一个 Server，原 Server 上的后台会话随后结束），请求不发出，该对话的记录保持不变，之后在它自己的连接下读取详情时更新（例如切换回原 Server 并打开该对话）。这样不会用其他 Server 的地址与凭据查询，也不会把其他 Server 的数据写入这条记录。
10. 保存与恢复：storage 不为 null 时，每个对话未结算 Turn 的记录（推送用量、最大序号、是否收到结束消息）保存在键 `agw:turn-usage:v1:${JSON.stringify([serverId, projectId, conversationId])}` 下：第 4、5 条修改记录后写入，第 8 条删除记录后移除。store 第一次为某个对话建立记录时读取保存的内容；恢复的 Turn 没有开始序号，按第 7 条只在列入 `unsettledTurnIds` 时计入，没有基线时全部计入；恢复的已结束 Turn 在采用基线后按第 8 条继续同步。`ExecutionSession` 在消息处理成功后才更新内存与存储中的 durable cursor，失败之后的消息等待恢复订阅（第 4 节）；页面在用量与 cursor 两次写入之间关闭时，这条消息在恢复后被补发，由第 4 条去重。

各场景的结果：

- 打开对话后发送消息：显示基线加运行中 Turn 的推送用量；Turn 结束后的同步把显示值更新为服务端累计值。
- Turn 运行中重新打开对话：新基线把这个 Turn 列在 `unsettledTurnIds` 中，且不含它的用量，已经推送的用量继续计入。
- Distributed 模式下运行中刷新、继续等待审批：刷新前推送的用量从 storage 恢复，基线把该 Turn 列为未结算，显示值与刷新前相同；审批后新推送的用量按序号继续计入，补发的旧消息被第 4 条忽略；Turn 结束后的同步给出服务端累计值。
- InProcess 模式下运行中刷新：恢复的推送用量继续显示；`RecoverInProcessExecution` 确认原执行空闲后，第 6 条的同步给出服务端累计值。开启 `resultOnly`，或在第一条 `UsageContent` 到达之前刷新时，没有可恢复的推送用量，面板先显示基线；基线把该 Turn 列为未结算，确认空闲后同样同步。
- Distributed 模式下，租约过期后由另一实例接管、接管者又被中断：旧 worker 先退出时不能结算（5.1 节），该 Turn 仍列为未结算，推送用量继续计入；接管者退出或到达截止时间后，第 8 条的同步给出服务端累计值。
- Distributed 模式下断线期间 Turn 结束，之后刷新页面：基线已经包含这个 Turn，它不在 `unsettledTurnIds` 中，恢复的记录与补发的 `UsageContent` 都不计入，按第 8 条删除；补发的结束消息触发一次同步。
- 刷新发生在收到开始消息之前：补发包含开始消息，这个 Turn 在补发期间计入；补发的结束消息触发的同步完成后，按第 8 条删除它的记录，显示服务端累计值。
- Distributed 中断，worker 在其他实例上：结束后的同步响应仍把该 Turn 列为未结算，推送用量继续计入；worker 退出（或租约时长过去）之后，第 8 条的同步给出包含该 Turn 全部用量的值。
- Desktop 切换 Server 之后，原 Server 上的后台会话结束：按第 9 条不发同步请求；切换回原 Server 并打开该对话时更新。
- 重复投递（例如 `reconcileStartFailure` 以空 cursor 重新订阅）：按第 4 条去重。
- `resultOnly` 与启用 summary 的 Turn：运行期间显示值不含没有推送的用量，Turn 结束后的同步把它们计入。
- Turn 结算时仍在运行的后台 Agent 任务：任务完成后写入的用量在下一次读取对话详情时计入，例如下一个 Turn 结束后的同步或重新打开对话。
- 清空对话记录、恢复 Agentflow checkpoint：用量不变。服务端 `agent_usage` 保留已经消耗的 token，之后的同步返回同一个累计值；恢复 checkpoint 时不再按保留的消息重新计算。
- 连接中断、没有收到结束消息：显示值保持不变，下一次读取对话详情时更新。

每个产生过事件的对话在内存中保留一条记录，直到页面或应用关闭；storage 中的记录在对应 Turn 被基线包含时移除。用量变化时通知 `subscribe` 的函数，`get` 返回的对象在用量不变时保持同一个引用。

#### 5.4 时序

```mermaid
sequenceDiagram
  participant WS as ChatWorkspace
  participant C as ChatHttpClient
  participant Server as Agw Server
  participant U as ConversationTokenUsageStore
  participant UI as ChatAside
  WS->>C: getConversation(scope)
  C->>Server: GET 对话详情
  Server-->>C: usage（已结算）与 unsettledTurnIds
  C-->>U: response 事件
  U-->>UI: 显示基线
  Note over C,Server: 用户发送消息，Turn T 开始
  Server-->>C: ReceiveMessage（agw-turn-start，T）
  C-->>U: message 事件，记下 T 的开始序号
  Server-->>C: ReceiveMessage（UsageContent，T，turnSequence）
  C-->>U: message 事件，计入 T 的推送用量
  U-->>UI: 显示基线加 T 的推送用量
  Server-->>C: ReceiveMessage（agw-turn-finished，T）
  C-->>U: message 事件
  U->>C: getConversation(scope)
  C->>Server: GET 对话详情
  Server-->>C: usage（包含 T 的全部用量）
  C-->>U: response 事件，删除 T 的推送用量
  U-->>UI: 显示服务端累计值
```

### 6. Web 与 Desktop 的 Chat

`packages/chat/src/ui-web/components/message/chat.tsx`：

- 删除 `conversationUsage` state 与 `setConversationUsage` 的三处调用（会话水合、`applyExecutionMessage` 中的 `getMessageTokenUsage`、`handleResumeCheckpoint`），删除 `calculateConversationUsage`，以及因此不再使用的 `addTokenUsage`、`EMPTY_TOKEN_USAGE`、`getMessageTokenUsage`、`TokenUsage` 导入。
- 增加 `const conversationUsage = useConversationTokenUsage(queueKey)`，`queueKey` 为现有的 `{ serverId: executionServerId, projectId, conversationId: executionConversationId }`，结果传给 `ChatAside`。`ChatAside` 不变。
- `ChatSessionSeed` 删除 `usage` 字段。
- 对话 HTTP 改为 `chatHttpClient`：
  - 权限能力查询：`chatHttpClient.getPermissionCapabilities({ serverId: executionServerId, projectId, conversationId: executionConversationId }, target, signal)`；
  - Agent 建议：`chatHttpClient.getAgentSuggestions(scope, suggestionQueryParams, signal)`，`scope` 同上；
  - 用户输入导航：`chatHttpClient.getConversationTurnInputs({ serverId: executionServerId, projectId, conversationId: persistedConversationId }, signal)`，查询在 `projectId` 与 `persistedConversationId` 都存在时启用；
  - 加载更早的历史（`loadOlderMessages`、`loadHistoryThroughInput`）：`chatHttpClient.getConversationMessages({ serverId: executionServerId, projectId: activeProjectId, conversationId: activeConversationId }, options)`；
  - 清空记录：`chatHttpClient.clearConversationRecords({ serverId: executionServerId, projectId, conversationId: conversationToClear })`。
  - 删除不再使用的 `@agw/projects` 导入 `clearProjectConversationRecords`、`getProjectConversationMessages`、`getConversationTurnInputs` 与 `@agw/api` 导入 `apiGet`。

`packages/chat/src/ui-web/pages/chat/chat-workspace.tsx`：

- `loadConversationHistory` 改为 `chatHttpClient.getConversation(scope, abortController.signal)` 与 `chatHttpClient.getConversationMessages(scope, { direction: "older", pageSize: 50, signal: abortController.signal })`，`scope` 为 `{ serverId: executionServerId, projectId, conversationId: nextConversationId }`。
- 初始 state、`clearLocalSessionState`、切换 Project 时的 `replaceChatSession` 与 `loadConversationHistory` 中的 `ChatSessionSeed` 都删除 `usage`；删除不再使用的 `EMPTY_TOKEN_USAGE`、`getProjectConversationDetails`、`getProjectConversationMessages` 导入。

`packages/agents` 的 `execute-agent-drawer.tsx` 与 `execute-agentflow-drawer.tsx`：`sessionSeed` 删除 `usage`，删除 `EMPTY_TOKEN_USAGE` 导入。

### 7. Mobile

`packages/chat-native/src/native-workspace-provider.tsx` 的 `NativeWorkspaceSession`：

- 创建实例：

```ts
const chatClient = React.useMemo(
  () =>
    verifiedServer
      ? new ChatHttpClient({
          api: verifiedServer.client,
          identity: () => ({
            baseUrl: verifiedServer.profile.serverUrl,
            token: verifiedServer.token,
          }),
          execution: () => ({
            baseUrl: verifiedServer.profile.serverUrl,
            token: verifiedServer.token,
            withCredentials: false,
            webSocket: globalThis.WebSocket,
            attachmentStore: null,
          }),
        })
      : null,
  [verifiedServer],
);
```

Server 的 endpoint 或身份变化时，`NativeWorkspaceProvider` 已经用新的 `workspaceScope` 重建 `NativeWorkspaceSession`，实例随之重建。

- 作用范围的 `serverId` 为 `verifiedServer.profile.id`。
- `conversationDetailsQuery` 改为 `chatClient.getConversationHistory({ serverId, projectId: selectedProjectId, conversationId: selectedConversationId }, signal)`。
- 清空记录改为 `chatClient.clearConversationRecords(scope)`。
- 权限能力与 Agent 建议查询改为 `chatClient.getPermissionCapabilities` 与 `chatClient.getAgentSuggestions`，scope 为 `{ serverId, projectId: selectedProjectId, conversationId: readyConversationId }`。
- 对话列表的读取、重命名、删除仍使用 `conversationService`。

`packages/chat-native/src/native-execution-session.ts`：

- `NativeExecutionSessionOptions` 的 `serverUrl`、`token` 改为 `client: ChatHttpClient` 与 `scope: ChatConversationScope`；构造函数用 `options.client.createExecutionSession(options.scope, handlers)` 创建会话。连接配置（`withCredentials: false`、`webSocket`、`attachmentStore: null`）移到上面的 `execution` 中。
- `ensureConfiguredSession` 创建会话时传入 `chatClient` 与 `{ serverId, projectId: selectedProjectId, conversationId }`。
- chat-native 从 `@agw/chat-runtime/chat-http-client` 导入 `ChatHttpClient` 与 `ChatConversationScope`。

### 8. Desktop extension

#### 8.1 权限

- `packages/extension-sdk/src/permissions.ts`：`EXTENSION_PERMISSIONS` 增加 `"chat.observe"`，并加入 `HIGH_RISK_EXTENSION_PERMISSIONS`。manifest 的 zod schema 使用这个列表。
- `desktop/renderer/src/runtime/desktop-extensions-page.tsx` 的 `PERMISSION_DESCRIPTIONS` 增加 `"chat.observe": "Observe chat traffic on the current Server: requests sent by chat and every message the Server returns, in all conversations."`。
- 批准规则不变：启用前批准全部声明；Reload 或替换安装后新增 `chat.observe` 时停用并等待重新批准。

#### 8.2 Extension API

`packages/extension-sdk/src/api.ts`：

```ts
import type { ChatEvent, ChatEventFilter } from "@agw/execution-core";

export interface ExtensionApi {
  // 其他成员不变。Other members are unchanged.
  readonly chat: {
    composer: { insertText(text: string): Promise<void> };
    /**
     * 订阅当前 Server 上全部对话的通信事件；需要 chat.observe。宿主接受订阅后返回。
     * Subscribes to the traffic events of every conversation on the current Server; requires chat.observe.
     * Resolves once the host accepts the subscription.
     */
    observe(filter: ChatEventFilter, listener: (event: ChatEvent) => void): Promise<Disposable>;
  };
}
```

- 与 `ExecutionMessage` 相同，`api.ts` 用 `export type` 转出 `ChatEvent`、`ChatEventFilter`、`ChatScope`、`ChatEventOrigin`、`ChatChannel`、`ChatEventKind`、`ChatMessageSource`、`ChatRequestEvent`、`ChatResponseEvent`、`ChatMessageEvent`、`ChatHttpRequest`、`ChatExecutionRequest`、`ChatEventError`、`ExecutionHubMethod`，生成 `sdk.d.ts` 时一起打包。
- 错误：
  - `ERR_FRAME_CLOSING`：所属 frame 正在关闭；
  - `ERR_PERMISSION_DENIED`：没有 `chat.observe`；
  - `ERR_INVALID_ARGUMENT`：条件不合法；
  - `ERR_TOO_MANY_SUBSCRIPTIONS`：该 extension 已有 16 个订阅。
- 宿主接受订阅后立即开始推送，`listener` 可能在 `observe` 返回的 Promise 完成之前收到事件。
- 订阅在以下情况结束，之后不再收到事件：调用 `dispose`；所属 frame 关闭、页面被替换、extension 停用。连接标识变化时订阅保留，之后只收到属于新连接的事件（8.4 节）。
- `ExecutionSessionApi` 增加 `readonly readOnly: boolean`：在创建时确定。对话已经保存、没有开放给 extension、且由其他一方（Desktop、Web、Mobile、Job、A2A 或其他 extension）创建时为 true。`createSession` 的文档注释说明只读会话允许创建、各写入方法受只读规则限制，并写明 `projectId` 与 `conversationId` 必须是小写 UUID。
- 会话的 `onMessage` 接收自己连接的消息与这台 Desktop 上其他会话收到的同一对话实时消息（8.6 节）。`send`、`interrupt`、`respond`、`setPermissionMode` 的错误说明增加 `ERR_CONVERSATION_READ_ONLY`。

#### 8.3 SDK

`packages/extension-sdk/src/sdk/api.ts` 的 `chat.observe` 与 `a2a.stream` 的写法相同：

1. 用 `crypto.randomUUID()` 生成 `subscriptionId`，先在事件路由中订阅 `chat.event`，只处理 `subscriptionId` 相同的事件，把其中的 `event` 交给 `listener`；
2. 请求 `chat.observe`，参数为 `{ subscriptionId, filter }`；请求失败时取消第 1 步的路由订阅并抛出；
3. 返回的 `dispose` 取消路由订阅并请求 `chat.unobserve`（`{ subscriptionId }`），该请求的失败用 `console.error` 输出。

执行 API 的 `createSession` 从 RPC 结果读取 `readOnly` 并放到会话对象上。

#### 8.4 broker

`desktop/renderer/src/runtime/extensions/extension-bridge-broker.ts`：

- `ExtensionBridgeBrokerOptions` 增加 `chat: Pick<ChatHttpClient, "subscribe">`，`ExtensionHost` 传入 `chatHttpClient`。
- `FrameConnection` 增加 `chatSubscriptions: Map<string, () => void>`，记录 `subscriptionId` 与取消订阅的函数。
- `chat.observe`（`{ subscriptionId: string; filter: ChatEventFilter }`）依次检查：
  1. `isFrameClosing(frameId)` 为 true 时返回 `ERR_FRAME_CLOSING`；
  2. `requirePermission(connection, "chat.observe")`；
  3. `subscriptionId` 必须是 1 到 64 个字符的字符串，且当前连接没有使用过，否则返回 `ERR_INVALID_ARGUMENT`；
  4. `validateChatEventFilter(filter)` 返回问题时返回 `ERR_INVALID_ARGUMENT`，信息为问题描述；
  5. 该 extension 全部连接上的订阅数达到 `MAX_CHAT_SUBSCRIPTIONS_PER_EXTENSION`（16）时返回 `ERR_TOO_MANY_SUBSCRIPTIONS`。

  检查通过后调用 `chat.subscribe(filter, listener)`，把返回的函数存入 `chatSubscriptions`，返回 null。

- `listener` 收到事件后：
  1. 没有连接，或事件不属于当前连接时丢弃。属于当前连接要求三项都相等，与 `connectionKeyOf` 使用的字段相同：`event.scope.serverId` 等于 `context.connection.profileId`（profile id 也是 `ExecutionPlatformProvider` 的 `serverId`）；连接身份的 `baseUrl`、`token` 等于 `context.connection` 的 `baseUrl`、`token`。同一 profile 修改地址或更换身份后，按旧配置创建、仍在后台运行的会话的事件，以及修改之前发出、之后才返回的 HTTP 请求的事件，都因 `baseUrl` 或 `token` 不同而被丢弃；
  2. 调用 `redactChatEventForExtension(event)`：`channel` 为 `execution`、`request.method` 为 `DispatchCommand` 且 `request.args[0].type` 为 `SettingCommand` 的 request 与 response 事件，返回去掉命令中 `environmentVariables` 字段的冻结副本；其他事件原样返回；
  3. 调用 `connection.endpoint.emit("chat.event", { subscriptionId, event })`。
- `chat.unobserve`（`{ subscriptionId: string }`）：取消并删除该订阅，返回 null；当前连接没有该订阅时也返回 null，`dispose` 可以重复调用。
- `disconnect` 取消该连接的全部订阅。
- `execution.createSession` 在权限与 frame 检查之后校验参数：`projectId` 必须是小写 UUID，给出 `conversationId` 时也必须是小写 UUID。失败时返回 `ERR_INVALID_ARGUMENT`，请求尚未发出。
  - 这两个 id 进入详情请求路径。`encodeURIComponent` 保留 `.` 与 `..`，URL 解析会去掉这些点段；例如 `projectId: "."`、`conversationId: ".."` 会变成 `GET /api/projects/`，响应经通信事件到达持有 `chat.observe` 的 extension。UUID 校验保证详情请求只能到达对应接口。
  - Server 与 `createUuidV7` 返回小写 id，会话与事件的作用范围按字符串相等匹配。

```mermaid
sequenceDiagram
  participant Ext as extension（SDK）
  participant B as ExtensionBridgeBroker
  participant C as ChatHttpClient
  Ext->>B: request chat.observe { subscriptionId, filter }
  B->>B: frame 状态、chat.observe 权限、参数、订阅数
  B->>C: subscribe(filter, listener)
  B-->>Ext: response null
  C-->>B: 匹配的事件
  B->>B: 检查当前 Server，去掉 SettingCommand 的 environmentVariables
  B-->>Ext: event chat.event { subscriptionId, event }
  Ext->>B: request chat.unobserve { subscriptionId }
  B->>C: 取消订阅
  B-->>Ext: response null
```

#### 8.5 extension 发起的执行会话

- `desktop/renderer/src/runtime/extensions/execution-service.ts`：`ExecutionServiceOptions` 的 `createSession?(handlers, runtime)` 改为 `chat: Pick<ChatHttpClient, "createExecutionSession" | "getConversation" | "subscribe">`；`ExecutionSessionLike` 保持现有定义。
- `createSession` 在 `context.request` 的回调中、创建执行会话之前确定 `readOnly`。`scope` 为 `{ serverId: identity.profileId, projectId: options.projectId, conversationId }`，`origin` 从 `owner.extensionId` 构造：
  - 没有传入 `conversationId`：新对话，`readOnly` 为 false，不发详情请求。
  - 传入 `conversationId`：调用 `chat.getConversation(scope, requestSignal, origin)`；成功时 `readOnly = !(details.openToExtensions || details.extensionId === owner.extensionId)`。
  - `ApiError` 的状态为 404、响应体错误码为 4040007：草稿对话，`readOnly` 为 false。
  - 其他失败继续抛出，执行会话尚未创建。
- 随后创建执行会话：

```ts
session = this.options.chat.createExecutionSession(
  { serverId: identity.profileId, projectId: options.projectId, conversationId },
  handlers,
  { runtime, origin: { kind: "extension", extensionId: owner.extensionId } },
);
```

`runtime` 仍为现有的 `{ baseUrl: identity.baseUrl, token: identity.token, attachmentStore: null, webSocket: TrackedWebSocket }`。

- `ExtensionHost` 构造 `ExecutionService` 时传入 `chatHttpClient`。
- 这些会话的事件 `origin` 为 `{ kind: "extension", extensionId }`。订阅方（其他 extension、该 extension 自己、`ConversationTokenUsageStore`）都会收到，可以用 `origins` 条件或 `origin.extensionId` 选择。
- 返回 `{ sessionId, conversationId, readOnly }`，`readOnly` 经 RPC 原样传递。它在创建时确定，最终写入权限由 Server 判定；开放对话在创建会话之后被其他 extension 写入时，发送会收到 `ERR_CONVERSATION_READ_ONLY`。
- `send`、`interrupt`、`respond`、`setPermissionMode` 收到包含 `HubException: 4030008:` 的错误时，转换为 `createExtensionError("ERR_CONVERSATION_READ_ONLY", "The conversation is read-only for this extension.")`。`send` 的这个判断在 `ERR_EXECUTION_BUSY` 判断之前。
- 进程内模式下，只读会话发现宿主 Turn 正在运行时，`send` 的本地忙碌检查返回 `ERR_EXECUTION_BUSY`；Turn 结束后发送，由 Server 返回 `ERR_CONVERSATION_READ_ONLY`。extension 可以读取 `readOnly` 提前显示状态。
- 会话自己连接上的 `execution.message` 投递与对话列表刷新保留现有流程。

#### 8.6 转发对话的实时消息

每个 extension 会话，包括只读会话与可写会话，都可以接收这台 Desktop 上宿主会话（Chat、执行抽屉）与其他 extension 会话收到的同一对话实时消息。

```mermaid
sequenceDiagram
  participant HostSession as 宿主的 ExecutionSession
  participant C as ChatHttpClient
  participant S as ExecutionService
  participant B as ExtensionBridgeBroker
  participant Ext as extension frame
  HostSession->>C: observer.message(message)
  C->>S: message 事件（scope、origin、连接身份）
  S->>S: 同一对话、同一连接身份、来源为其他一方
  S->>B: deliver(owner, "execution.message", { sessionId, message })
  B-->>Ext: execution.message
```

- `createSession` 在会话配置成功并登记之后订阅：

  ```ts
  chat.subscribe(
    {
      kinds: ["message"],
      sources: ["live"],
      serverId: identity.profileId,
      projectId,
      conversationId,
    },
    listener,
  );
  ```

- 订阅函数依次检查：
  1. `origin.kind === "extension" && origin.extensionId === owner.extensionId` 时跳过，该 extension 的会话通过自己的连接接收消息；
  2. 事件连接身份的规范化 `baseUrl` 与 `token` 必须等于会话创建时的身份；
  3. 调用 `deliver(owner, "execution.message", { sessionId, message: event.message })`。
- 会话关闭（`dispose`、frame 关闭、页面替换、extension 停用、连接身份变化）时取消订阅；创建失败时清理订阅。
- 转发内容是 ChatHttpClient 派发的服务端原始消息。转发不改变会话自己的执行状态，对话列表仍由源会话刷新。
- 宿主在 Chat 中打开、发送该对话，或后台 Turn 仍有会话时，宿主会话收到的 live 消息可被转发。范围由这台 Desktop 上实际收到的消息决定。
- 权限使用创建会话所需的 `execution`。

#### 8.7 保留名称 `host`

`desktop/src/shared/extension-manifest.ts`：

- `id` 的 `EXTENSION_ID_PATTERN` 加入保留名称 `host`：`/^(?!agw)(?!(?:data|packages|host)$)[a-z0-9]+(?:-[a-z0-9]+)*$/u`。`id` 只允许小写字母，`HOST`、`Host` 同样校验失败。注释说明 `host` 表示宿主创建的对话。
- `name` 的 `z.string().min(1).max(64)` 之后增加 `regex(/^(?!\s*[Hh][Oo][Ss][Tt]\s*$)/u)`，去掉首尾空白后忽略大小写等于 `host` 时校验失败。模式显式列出大小写：`z.toJSONSchema` 输出模式的 `source`，生成的 `extensionManifestJsonSchema` 执行相同检查。
- 校验失败时 `evaluateExtensionManifest` 返回 `invalid`，错误路径为 `id` 或 `name`；`readExtensionDirectory` 将已安装的这类 extension 标为无效，不会加载。

### 9. Server：请求方身份

`Agw.Agents.Execution/Commands/Setting`：

- `SettingCommand` 增加构造参数 `string? extensionId = null` 与属性 `ExtensionId`，`==` 与 `GetHashCode` 包含该字段，字符串按序号对比。
- `SettingCommandMapper` 校验：
  - 没有值：连接代表宿主；
  - 与 `ConversationCreators.Host` 忽略大小写相等：抛出 `AgwException(ErrorCodes.InvalidParam, "SettingCommand.extensionId cannot be \"host\".")`；
  - 长度不在 3 到 64 之间，或不匹配 `^[a-z0-9]+(?:-[a-z0-9]+)*\z`：抛出 `AgwException(ErrorCodes.InvalidParam, "SettingCommand.extensionId must have 3 to 64 lowercase letters, digits and single hyphens.")`。
- 模式使用静态 `Regex` 字段，与 `AgentTurnResultText` 相同；`\z` 要求字符串完全结束。程序集的类型命名需要满足 `ExecutionTypeNames_UseLayerSuffixes`。
- Desktop 安装目录的 `agw` 前缀、`data`、`packages` 保留规则由 Desktop 执行。

`Agw.Agents.Execution/Runtimes/ExecutionSettings.cs`：

- 构造函数最后增加 `string? extensionId = null` 与属性 `ExtensionId`；每个 `With*` 方法复制它，`Equals` 与 `GetHashCode` 包含它。
- 身份变化按设置变化处理：`ApplySettingsAsync` 要求连接空闲、释放 runtime、清除 `_resolvedTask`，使为一个请求方解析的任务不会被另一个请求方复用。
- 请求方身份保存在连接设置中。Durable 的写入检查在 hub 所在节点、登记之前完成；checkpoint 恢复使用发起恢复的连接身份。`DurableExecutionMapper.FromSettings` 不把 `ExtensionId` 写入 `DurableExecutionSettings` 或 manifest，`ToRuntimeSettings` 产生的 worker 设置中 `ExtensionId` 为 null。checkpoint 恢复会复制源 Turn 的 manifest，省略身份使恢复分支的请求方始终由发起恢复的连接确定，不会继承源 Turn 的请求方身份。

`SettingCommand` 在配置与每次重连恢复时先于后续命令发送，写入操作从连接设置取得同一个身份。不合法字段由 `DispatchCommand` 返回 `4000001`，`createSession` 可以接收错误码。

身份随任务解析传递：

```mermaid
flowchart LR
  A["TurnAcceptanceService.ResolveTaskAsync<br/>request.Settings.ExtensionId"] --> B["ResolveProjectTaskRequest.RequesterExtensionId"]
  B --> C["ProjectTaskFacade.ResolveAsync"]
  C --> D["ExecutionTaskRequest.RequesterExtensionId"]
  D --> E["TaskAppService.CreateTaskForExecutionCoreAsync / CreateTaskAsync"]
  E --> F["TaskExecutionAppService.CreateForExecutionAsync / CreateAsync"]
  F --> G["GetOrCreateConversationAsync(requesterExtensionId)"]
```

- `ResolveProjectTaskRequest`、`ExecutionTaskRequest` 各增加位置参数 `string? RequesterExtensionId`，宿主传 null。
- `TaskAppService` 的三条创建路径传入 `request.RequesterExtensionId`，公开的 `CreateTaskForExecutionAsync` 传 null。
- `TaskExecutionAppService` 内部的 `CreateForExecutionAsync` 与私有 `CreateAsync` 接收显式的 `requesterExtensionId`；Jobs 与 A2A 的公开入口传 null。
- 创建方在解析任务时确定，受理事务沿用 `TurnAcceptanceWriter`、`ConversationTurnStore.AcceptAsync` 的现有流程。

### 10. Projects：创建方与写入规则

#### 10.1 写入规则与并发

```mermaid
flowchart TD
  Start["请求方 R 写入对话 C"] --> Host{"R 是宿主？"}
  Host -- "是" --> Allow["允许"]
  Host -- "否" --> Draft{"C 已保存？"}
  Draft -- "否（草稿）" --> AllowCreate["允许；解析任务时插入 C，<br/>extension_id = R"]
  Draft -- "是" --> Own{"C 的创建方是 R？"}
  Own -- "是" --> Allow
  Own -- "否" --> Open{"创建方为 host、generation 为 0、<br/>没有已受理的 Turn？"}
  Open -- "是" --> AllowClaim["允许；解析任务时在对话行锁内<br/>extension_id = R"]
  Open -- "否" --> Refuse["拒绝：ConversationReadOnly"]
```

- 宿主可以继续任何对话，在草稿中解析第一个任务时记录创建方为 `host`。
- extension 可以继续自己创建的对话。宿主对话中已有受理 Turn，或记录清空过时，extension 只读；其他 extension 创建的对话同样只读。
- 宿主第一个 Turn 受理失败时，对话保持开放，第一个在其中解析任务的 extension 成为创建方。
- extension 第一个 Turn 受理失败时，创建方已经记录，同一 extension 可以重试，其他 extension 只读。
- 宿主已经插入对话行、Turn 尚未受理时，对话仍然开放；extension 可以在这段时间成为创建方，宿主随后继续受理。

并发判断以同一对话行的存续期间为范围。extension 成为创建方之后保持不变，`generation` 只增加；对于创建方仍为 `host`、`generation` 仍为 0 的对话，一旦存在已受理 Turn，后续保留对话行的事务必须保留至少一条 Turn，或增加 `generation`。各删除路径满足以下条件：

- 清空记录：`ProjectDeletionCoordinator.ClearConversationRecordsAsync` 在同一事务中增加 `generation` 并删除全部 Turn，创建方保持不变。提交后的对话因 `generation > 0` 保持关闭。
- InProcess checkpoint 恢复：`AgentflowRuntime.CheckpointState` 创建时为空，只登记该 runtime 实际记录的 checkpoint。`AgentflowTurnExecutor` 在已受理 Turn 的执行过程中记录 checkpoint；`AgentflowCheckpointStore.RecordAsync` 先经过 history barrier，再分配 checkpoint marker 的 `ConversationSequence`，以最后一个 marker 的序号作为 `BoundarySequence`。受理时分配的 `FirstSequence` 不大于这个边界，即使该 Turn 没有输入历史行也成立。`AgentflowCheckpointPersistence.DeleteConversationHistoryAfterAsync` 只删除 `FirstSequence > BoundarySequence` 的 Turn，保留生成该 checkpoint 的已受理 Turn；调整保留 Turn 的 `LastSequence` 同样不会删除它。`ResumeCheckpointAsync` 还要求 checkpoint 存在于当前 runtime，恢复后移除该 runtime 中边界之后的 checkpoint。
- Distributed checkpoint 恢复：`PrepareDistributedResumeAsync` 在同一事务中删除边界之后的历史、Turn 与 checkpoint，调用 `AcceptResumeTurnAsync` 受理新的恢复 Turn，并登记 Durable 执行与开始事件。事务提交后仍存在已受理 Turn。
- 启动升级：`DurableTurnUpgrade.UpgradeExecutionAsync` 在同一事务中删除需要替换的旧 Turn，并创建或更新对应的活动 Turn 后提交。Host 在 `app.StartAsync` 之前完成升级，数据库也不会出现已提交的替换中间状态。
- 删除对话、删除项目内全部对话、删除 Project：`ProjectDeletionCoordinator` 在同一事务中删除 Turn 与对应对话行，该对话行的存续期结束。之后使用同一 `conversationId` 创建的对话需要重新判断创建方；第 11 节要求每条写入命令重新检查，包括持有 `_resolvedTask` 的连接。

权限检查的创建方、`generation` 与 Turn 存在性必须来自同一快照。第 10.4 节的 facade 用一条带 `EXISTS` 的 SQL 同时投影这三项。对创建方为 `host`、清空前已有已受理 Turn 的对话，extension 的权限查询与清空记录交错时，读取到清空前的状态（`generation == 0`、有 Turn）或清空后的状态（`generation > 0`、没有 Turn），两种状态都判为只读。

在同一快照与上述删除规则的前提下，同一对话行已经关闭的开放状态不会恢复，“创建方是请求方”与“只读”的判断可以直接使用。开放的判断可能在记录创建方或受理 Turn 后失效：记录 extension 创建方时必须取得对话行锁，在锁内重新读取创建方、`generation` 与是否有已受理 Turn。`SaveConversationChangesAsync`、清空记录与创建方记录使用同一组项目锁与对话行锁，按顺序执行。两个 extension 同时写入时，先取得锁的一方成为创建方，后一方读取该结果并被拒绝，无法覆盖创建方，也就无法通过这次写入中断前一方的 Turn。

#### 10.2 实体与 Behavior

- `ProjectConversation.ExtensionId` 为 `string`，初始值为 `ConversationCreators.Host`。`ProjectConversationConfiguration` 使用 `IsRequired().HasMaxLength(64).HasDefaultValue(ConversationCreators.Host)`，与 `Title` 的默认值写法相同。
- 表继续由 Agw.Projects 管理。
- `ProjectConversationBehavior` 与现有 `EnsureIdentity` 使用相同的检查方式，接收调用方查询得到的 `hasAcceptedTurns`：

```csharp
/// <summary>
/// 检查请求方是否可以写入对话。
/// Checks whether the requester may write the conversation.
/// </summary>
public void EnsureWritableBy(string? requesterExtensionId, bool hasAcceptedTurns)

/// <summary>
/// extension 在开放对话中解析任务时记录为创建方。
/// Records an extension as creator when it resolves a task in an open conversation.
/// </summary>
public void RecordCreatorIfOpen(string? requesterExtensionId, bool hasAcceptedTurns)

/// <summary>
/// 判断对话是否由这个 extension 创建。
/// Checks whether the conversation was created by this extension.
/// </summary>
public bool IsCreatedBy(string requesterExtensionId)
```

- 开放条件：`ExtensionId == ConversationCreators.Host && Generation == 0 && !hasAcceptedTurns`。
- `IsCreatedBy`：`ExtensionId` 与 `requesterExtensionId` 按序号相等。
- `EnsureWritableBy`：宿主（`requesterExtensionId` 为 null）、`IsCreatedBy` 成立、开放条件成立时允许；其他情况抛出 `AgwException(ErrorCodes.ConversationReadOnly)`。第 8.7、9 节的保留名称校验保证 extension 的 `requesterExtensionId` 不会等于 `host`，因此 extension 不会与宿主创建方相等。
- `RecordCreatorIfOpen`：`requesterExtensionId` 不为 null 且开放条件成立时，设置 `ExtensionId = requesterExtensionId`。

#### 10.3 解析任务时记录创建方

`TaskExecutionAppService.GetOrCreateConversationAsync`：

- 新对话初始化 `ExtensionId = requesterExtensionId ?? ConversationCreators.Host`，随 `SaveConversationChangesAsync` 提交，与后续 Turn 是否受理成功无关。同一 `conversationId` 的并发插入通过主键冲突进入并发分支。
- 已有对话在 `ReloadAsync` 与 Detached 检查之后、修改 `ContextId` 与 `UpdateExistingConversation` 之前，对 extension 请求调用私有方法 `EnsureWritableByRequesterAsync`：
  1. `IsCreatedBy` 成立时继续；
  2. 查询 `ProjectConversationTurns`，通过 `EnsureWritableBy` 检查；
  3. 开放对话调用 `IConversationCreatorStore.ClaimOpenConversationAsync`，在锁内重新检查并记录创建方；返回之后再次 `ReloadAsync`，取得已提交的创建方。
- `ReloadAsync` 重置跟踪状态，拒绝时连接 DbContext 没有待保存的修改。创建方由行锁事务保存；调用方跟踪的实体不修改 `ExtensionId`，之后的 `SaveConversationChangesAsync` 只写入改变的属性，不会把此前读到的创建方写回。
- 并发插入分支在复用 `concurrentConversation`、调用 `UpdateExistingConversation` 之前执行同样的三步。
- `ExternalTaskSnapshotStore.SaveAsync`（A2A）、`ConversationHistoryStore.AppendRecordsCoreAsync`、Jobs 经 `GetOrCreateAsync` 创建的对话，通过实体默认值记录为 `host`。

`Agw.Projects/Application/Persistence/IConversationCreatorStore.cs` 的实现在 `Agw.Infrastructure/Projects/ConversationCreatorStore.cs`。与 `IProjectProviderSessionCoordinator` 的方式相同，Infrastructure 负责锁与保存，Application 提供委托并创建 Behavior：

```csharp
/// <summary>
/// 取得项目与对话行锁，读取当前状态并执行 Application 委托，保存创建方。
/// Takes project and conversation row locks, reads current state, runs the Application delegate and saves the creator.
/// </summary>
Task ClaimOpenConversationAsync(
    Guid conversationId,
    int expectedGeneration,
    Action<ConversationCreatorClaim> operation,
    CancellationToken cancellationToken = default
);

public sealed record ConversationCreatorClaim(ProjectConversation Conversation, bool HasAcceptedTurns);
```

`ConversationCreatorStore` 使用调用方 scope 的 `AgwDbContext`，在事务中依次执行，已有事务时加入它：

1. `LockOwnedProjectAsync` 锁定所属 Project。
2. 以 `SET generation = generation WHERE id = @id AND generation = @expectedGeneration` 取得对话行锁，所有者条件与 `SaveConversationChangesAsync` 相同。没有更新到一行时抛出 `ConversationSessionConflict`。
3. 在锁内通过 AsNoTracking 读取对话与是否有已受理 Turn，取得数据库中的当前状态。
4. 执行 Application 委托：在读取的对话上调用 `EnsureWritableBy`、`RecordCreatorIfOpen`。创建方已经相同时保持原值，其他已关闭对话抛出 `ConversationReadOnly` 并回滚事务。
5. 以 `ExecuteUpdate` 只写入 `extension_id` 并提交，调用方 DbContext 的其他修改保留待保存状态。

`SaveConversationChangesAsync` 与清空记录使用相同的项目锁与对话行锁，创建方变更与这些操作按顺序执行。

#### 10.4 跨模块检查接口

`Agw.Projects.Contracts/Execution/IProjectConversationAccessFacade.cs`：

```csharp
/// <summary>写入前检查请求方权限。Checks requester access before a write.</summary>
Task EnsureWritableAsync(Guid conversationId, string? requesterExtensionId, CancellationToken cancellationToken = default);
```

实现在 `Agw.Projects/Application/Facades/ProjectConversationAccessFacade.cs`（public sealed），依赖 `IProjectsDbContext`，在 `AddProjects` 注册为 scoped：

- 宿主直接返回。
- extension 与 `ConversationTurnStore` 读取用户的方式相同，通过 `UserInfoUtil.RequiredUserId` 取得用户，按 `Id == conversationId`、`CreateBy` 与用户相同、`Project.CreateBy` 与所有者相同查询，使用 AsNoTracking。在同一次 `Select` 中投影对话与 `ProjectConversationTurns.Any(turn => turn.ProjectConversationId == conversation.Id)`，生成一条包含 `EXISTS` 的 SQL；`ExtensionId`、`Generation` 与 `hasAcceptedTurns` 使用同一语句快照。
- 没有找到时按草稿处理；其他用户的对话保持相同表现，后续受理通过现有用户隔离规则以 `InvalidParam` 拒绝。
- 找到后使用投影得到的对话与 `hasAcceptedTurns`，调用 `new ProjectConversationBehavior(row).EnsureWritableBy(requesterExtensionId, hasAcceptedTurns)`；整个权限判定使用本次查询的结果。
- 规则由 Projects 实现。Agents 的测试通过 `TurnPersistenceTestKit` 组合实际的 Projects 实现。

### 11. Agents.Execution：写入检查与错误

`ExecutionConnectionContext` 在 `projectTasks` 之后增加必需构造参数 `IProjectConversationAccessFacade conversationAccess`。`ExecutionConnectionContextFactory` 从连接 scope 取得它，该 scope 的 DbContext 与 runtime scope 分开。检查均在推入用户 scope 之后执行。

```mermaid
flowchart TD
  Cmd["ExecCommand / ResumeCheckpoint / Interrupt / HumanResponse / SetMode / SetPermissionMode"] --> Gate{"EnsureWritableAsync<br/>(conversationId, Settings.ExtensionId)"}
  Recover["RecoverInProcessExecution(id, true)"] --> RGate{"目标连接的对话<br/>与调用方连接的 ExtensionId"}
  Gate -- "只读" --> Refuse["AgwException 4030008<br/>HubException"]
  RGate -- "只读" --> Refuse
  Gate -- "允许写入" --> Run["原有处理"]
  RGate -- "允许写入" --> Run
```

各命令的检查位置：

- `StartTurnAsync`：在 agentId、conversationId 与对话一致性检查之后，Durable 同一 turnId 重发的订阅、`EnsureIdleForNewTurnAsync`、写入 `WithConversationId` 与 `RefreshResolvedTaskAsync` 之前。每个 `ExecCommand` 都重新检查，缓存的 `_resolvedTask` 同样需要通过检查。进程内 checkpoint 恢复调用 `StartTurnAsync` 时再次检查。
- `ResumeCheckpointAsync`：参数检查之后、generation 检查之前，且在 `PrepareInProcessResumeAsync` 与 Durable 的 `_attachment.ResumeCheckpointAsync` 之前检查权限。`PrepareInProcessResumeAsync` 在事务内删除所选 `BoundarySequence` 之后的历史、`FirstSequence` 超过该边界的 Turn，以及 `BoundarySequence` 超过该边界的 checkpoint，保留所选 checkpoint；事务提交后才返回。后续创建恢复 Turn 失败时，这些已提交的删除不会自动撤销，因此权限检查必须在调用之前完成。
- `InterruptTurnAsync(turnId, reason)`、`SubmitHumanDecisionAsync`、`SetModeAsync`、`SetPermissionModeAsync`：设置中存在对话时首先检查，在 `_pendingModeChange`、权限校验、Durable 的 `InterruptAsync`/`RespondAsync`/`SetPermissionModeAsync` 与写入 `Settings` 之前。没有运行中的 Turn 时同样检查。
- 删除只由 `ExecutionConnection` 使用的 `InterruptTurnAsync(string? reason, CancellationToken)` 重载。中断逻辑放入私有 `InterruptCoreAsync(turnId, reason, cancellationToken)`；公开入口检查本连接请求方后调用它；新增 `internal Task InterruptForRecoveryAsync(string? requesterExtensionId, CancellationToken)` 检查给定请求方对本连接对话的权限，再以 `turnId: null` 调用核心逻辑。

跨连接中断：

- `ExecutionHub.RecoverInProcessExecution` 将 `Context.ConnectionId` 传给注册表，hub 方法保留原签名。
- `ExecutionConnectionRegistry.RecoverInProcessExecutionAsync(connectionId, userId, callerConnectionId, interrupt, cancellationToken)`：目标连接不存在或属于其他用户时返回 false；`interrupt` 为 true 时取得调用方连接，不存在或属于其他用户时抛出 `InvalidParam "Execution connection is not available."`。
- `ExecutionConnection` 增加 `internal string? RequesterExtensionId => _context.Settings?.ExtensionId`。SignalR 默认 `MaximumParallelInvocationsPerClient = 1`，调用方命令按顺序执行，可以直接读取。
- 目标连接的 `RecoverInProcessExecutionAsync(interrupt, requesterExtensionId, cancellationToken)` 在 `interrupt && HasActiveTurn` 时调用 `InterruptForRecoveryAsync`，检查被中断连接的对话。
- `interrupt` 为 false 的状态查询，及 `ApplySettingsAsync`、`SubscribeTurnAsync`、`GetAgentflowCheckpointsAsync`、`FindInProcessExecution`，在只读会话中继续可用。

`Agw.Shared/Exceptions/ErrorCodes.cs` 在 `FileNotWritable` 之后增加：

```csharp
public static readonly ErrorCode ConversationReadOnly = new(
    403_0008,
    "The conversation is read-only for this extension.",
    HttpStatusCode.Forbidden
);
```

拒绝由 extension 请求方与创建方的关系决定，使用 403。客户端收到 `HubException: 4030008: The conversation is read-only for this extension.`。

### 12. 公开创建方与对话列表

Server（`Agw.Projects/Contracts/TaskRequests.cs` 与 `ProjectConversationAppService`）：

- `ProjectConversationSummaryResponse` 与 `ProjectConversationResponse` 在 `ErrorMessage` 之后增加 `string ExtensionId`、`bool OpenToExtensions`，详情中位于 `Usage` 之前。
- `ExtensionId` 为创建方；`OpenToExtensions` 为 `ExtensionId == ConversationCreators.Host && Generation == 0 && 没有该对话的 Turn 行`。
- 列表的 `ConversationListRow` 与 `SelectConversationListRows` 增加这两项，在投影中通过 `EXISTS` 子查询计算开放状态，由 `ToSummaryResponse` 传入。
- 详情的 `ToResponseAsync` 传入 `conversation.ExtensionId`，查询是否有 Turn 后计算开放状态。
- 使用 `artifacts/openapi-export/export.sh` 导出 `openapi.json`，再运行 `pnpm gen:api`，覆盖创建方字段与 5.1 节的用量字段。

`packages/projects-core/src/task-client.ts`：

- 本地 `ProjectConversationSummaryResponse` 增加可选 `extensionId?: string`、`openToExtensions?: boolean`。
- `ConversationSummary` 增加 `extensionId: string | null`、`openToExtensions: boolean`。
- `toConversationSummary` 写入 `extensionId: conversation.extensionId ?? null`、`openToExtensions: conversation.openToExtensions ?? false`，`toConversationDetails` 通过展开取得它们。null 表示旧版 Server 没有返回创建方。
- 导出 `HOST_EXTENSION_ID`（`"host"`），供列表判断宿主创建的对话。

`lucide-react` 与 `lucide-react-native` 从 1.47.0 升级到 1.56.0，`MessageCircleText` 从 1.56.0 开始提供，1.47.0 的图标导出名称在 1.56.0 中全部保留，现有图标导入继续可用。对话列表在标题前显示创建方图标：

- 创建方为 `HOST_EXTENSION_ID`：`MessageCircleText`，名称为 `Created by Agw`。
- 创建方为 extension：`Puzzle`，名称为 `Created by extension "<extensionId>"`，显示 extension 的 id。列表由 Web 与 Desktop 共用，Web 没有已安装 extension 的信息，使用 id 可以在两端给出相同的标识。
- 创建方为 null：保留同样宽度的空白位置。
- 开放对话显示 `MessageCircleText`；extension 成为创建方之后，列表刷新时显示 `Puzzle`。
- Web 与 Desktop 的 `packages/projects/src/ui-web/components/task/conversation-list.tsx`：与行末的状态图标使用相同的尺寸与 Tooltip 方式，标题前 16px 位置、间隔 `mr-2`、颜色 `text-muted-foreground`；外层 `span` 设置 `role="img"` 与 `aria-label`。
- Mobile 的 `packages/chat-native/src/native-conversation-list.tsx`：宽 16、与标题间隔 10、颜色 `theme.muted`；外层 `View` 设置 `accessibilityRole="image"` 与 `accessibilityLabel`。

### 13. 兼容设计

- 服务端：
  - `SettingCommand` 与 `ExecutionSettings` 的 `extensionId` 默认值为 null，A2A 与 Jobs 的设置代表宿主；
  - 对话详情增加 `usage.unsettledTurnIds`、`extensionId` 与 `openToExtensions`，列表增加创建方与开放状态。`usage` 在 Turn 结算后计入；现有客户端读取计数字段时，运行期间基线不含该 Turn，结束后读取到的值包含它；
  - `IAgentUsageRecorder.AddAsync` 增加参数，两个调用方一起修改；`IConversationTurnStore.FinishAsync` 增加 `usageSettlesAt`，六个调用位置一起修改，并新增 `SettleUsageAsync`；
  - `agent_usage.turn_id` 可以为 null，已有记录不需要回填；`project_conversation_turn.usage_settles_at` 在迁移时为已结束的 Turn 填入结束时间；`durable_execution.interrupted_lease_epoch` 可以为 null，已有记录不需要回填；
  - 中断、结束消息的发送时机与内容不变。
- 已有对话的创建方由默认值填为 `host`：没有清空且没有已受理 Turn 的对话开放给 extension。升级之前由 extension 创建、已有受理 Turn 的对话，升级后按宿主对话执行只读规则，历史数据没有记录可用于识别原 extension。
- 已安装的 `id` 或 `name` 为 `host` 的 extension，在升级之后扫描时标为无效，不会加载。
- 没有发送 `SettingCommand` 的连接按宿主处理，符合 [ws-flow 的命令说明](../ws-flow.md#commands) 中“Without a prior Setting command…”的默认行为；宿主的 `ExecutionSession` 总是先配置。旧版 Desktop 的 extension 会话同样按宿主处理。
- 新版 Desktop 连接旧版 Server：Server 忽略未知 `extensionId` 字段，使用原有写入行为；详情缺少创建方与开放状态，extension 会话的 `readOnly` 为 true。
- 新客户端依赖 `unsettledTurnIds`、`turn_id` 与结算时间，需要与本次服务端一起升级。
- 客户端新增 localStorage 键 `agw:turn-usage:v1:...`（5.3 节第 10 条），与 durable attachment 的键 `agw:durable-execution:v2:...` 互不影响。
- `@agw/chat-runtime`：
  - `ExecutionSession` 构造函数增加有默认值的第三、第四个参数，HTTP 方法的 `origin` 为可选的最后一个参数；
  - `ExecutionSessionManager` 的 `ClientFactory` 增加第二个参数，只接收第一个参数的工厂仍然可用；
  - 新增 `ChatHttpClient`、`chatHttpClient`、`CONVERSATION_DETAILS_PATH`、`ConversationTokenUsageStore`、`conversationTokenUsage`、`useConversationTokenUsage`、`getExecutionRuntime` 与子路径 `./chat-http-client`。
- `@agw/chat`：`ChatSessionSeed` 删除 `usage`，宿主（`ChatWorkspace`、两个执行抽屉）与测试一起修改。
- `@agw/projects-core`：`getConversationTurnInputs` 增加可选参数；删除 `getProjectConversationHistory`、`ProjectConversationService.getProjectConversationHistory`、`ConversationDetails.usage`；创建方缺少时映射为 null，开放状态缺少时映射为 false。
- `@agw/chat-native`：`NativeExecutionSessionOptions` 的字段变化只影响包内代码。
- Desktop：`ExecutionServiceOptions` 与 `ExtensionBridgeBrokerOptions` 只在宿主内部使用。
- Extension API：`manifestVersion` 仍为 1。旧版 Desktop 的 manifest 校验不认识 `chat.observe`，声明它的 extension 在旧版中校验失败、不能启用；使用 `api.chat.observe` 的 extension 应把 `minAppVersion` 设为包含本功能的 Desktop 版本。
- 行为变化：恢复 Agentflow checkpoint 后，Token usage 保留恢复前的值并继续累加（第 5 节）。

### 14. 文档

- `src/clients/packages/execution-core/README.md`：职责中增加 `src/chat-events.ts`（通信事件类型、订阅条件的校验与匹配）。
- `src/clients/directory-structure.md`：`chat-runtime` 一行增加 ChatHttpClient 与 Token usage。
- `docs/2.Architecture.md`：`@agw/chat-runtime` 的说明增加 ChatHttpClient，实体关系增加 `ProjectConversation.ExtensionId`，Desktop extension 说明增加写入规则。
- `AGENTS.md`：Client architecture 中 `@agw/chat-runtime` 的说明增加 ChatHttpClient。
- `src/clients/desktop/README.md`：说明 `chat.observe` 权限与 `api.chat.observe`；`id`、`name` 的保留名称 `host`；执行会话的 `readOnly`、`ERR_CONVERSATION_READ_ONLY`、消息转发范围、宿主继续 extension 对话的规则及升级时的处理；`createSession` 的小写 UUID 要求与详情请求的 extension `origin`。
- `src/server/Agw.Agents.Execution/README.md`：Summary 的 token usage 一段增加“记录所属 Turn”；说明详情的已结算用量、`unsettledTurnIds`，及 Distributed 中断的结算时间与租约代次检查；命令表、`SettingCommandMapper` 校验与上下文增加 extension 身份和 `IProjectConversationAccessFacade`，说明创建方记录时机、开放条件、写入检查、进程内恢复及 `InterruptTurnAsync` 示例。
- `docs/ws-flow.md`：`SettingCommand.extensionId` 的格式与保留值、请求方规则、开放对话、拒绝操作与 `4030008`、跨连接中断检查、未配置连接的宿主身份。
- `CONTEXT.md`：增加 `Creator Extension` 与 `Open Conversation` 条目。
- SDK 文档注释：补充通信订阅、`readOnly`、消息范围、参数与错误说明。

## 接口文档

### 基础信息

- Agw Server 的对话列表与详情响应公开创建方和开放状态，详情的 `usage` 增加未结算 Turn；SignalR 的 `SettingCommand` 增加请求方身份，写入操作执行只读规则。
- 其余小节列出客户端包之间的接口与 Extension API。

### Server HTTP

- 修改接口：`GET /api/projects/{projectId}/conversations/{conversationId}`，认证方式与 Bens.Results 信封不变。
- 详情与 `GET /api/projects/{projectId}/conversations` 的每一项增加 `extensionId`（`host` 或 extension 的 `id`）、`openToExtensions`（是否满足开放条件）。
- `usage` 的计数字段只包含已结算 Turn 与不属于 Turn 的用量；新增 `usage.unsettledTurnIds`，列出没有计入的未结算 Turn，其中包括已经结束、用量尚未结算的 Turn（5.1 节）。
- 解包后的 `data` 示例，其余字段不变，省略：

```json
{
  "projectId": "11111111-1111-1111-1111-000000000001",
  "conversationId": "0199b6ef-1a2b-7c3d-8e4f-5a6b7c8d9e0f",
  "extensionId": "sample",
  "openToExtensions": false,
  "usage": {
    "inputTokenCount": 18100,
    "outputTokenCount": 836,
    "totalTokenCount": 18936,
    "cachedInputTokenCount": 8704,
    "reasoningTokenCount": 750,
    "unsettledTurnIds": ["0199b6f0-0000-7000-8000-000000000001"]
  }
}
```

- 兼容性见第 13 节。服务端修改后运行 `pnpm gen:api` 更新 `openapi.d.ts`。

### SignalR：SettingCommand

```json
{
  "type": "SettingCommand",
  "projectId": "0192f0c5-3c2a-7cc1-9d4e-6f1a2b3c4d5e",
  "conversationId": "0192f0c5-4b1d-7a10-8f2e-1c2d3e4f5a6b",
  "permissionMode": "alwaysAsk",
  "extensionId": "sample"
}
```

- 可选字段 `extensionId` 由 Desktop 为 extension 会话写入。宿主会话省略该字段。
- 值必须是 3 到 64 个小写字母、数字与单个连字符，忽略大小写不能等于 `host`；校验失败返回 `4000001`。
- 执行期间修改身份按设置变更以 `4090021` 拒绝。

### SignalR：写入操作的拒绝

extension 请求方写入已保存、创建方不同且未开放的对话时，以下操作返回 `HubException: 4030008: The conversation is read-only for this extension.`：

- `DispatchCommand` 的 `ExecCommand`、`ResumeCheckpointCommand`、`InterruptCommand`、`HumanResponseCommand`、`SetModeCommand`、`SetPermissionModeCommand`。
- `RecoverInProcessExecution(connectionId, true)`：使用调用方连接的身份检查目标连接的对话；调用方连接不存在或属于其他用户时返回 `4000001`。

### Extension manifest

- `id` 的保留名称包含 `host`。
- `name` 去掉首尾空白后忽略大小写不能等于 `host`。

### ChatHttpClient（`@agw/chat-runtime`）

- 类型与方法见 1.2 节；`subscribe` 的条件规则见第 3 节；`createExecutionSession` 见 1.3 节。

### 通信事件（`@agw/execution-core`）

- 类型见 2.1 节，`snapshotChatEvent` 见 2.4 节，`validateChatEventFilter` 与 `matchesChatEvent` 见第 3 节。
- message 事件示例：

```json
{
  "kind": "message",
  "id": "0199b6f0-7c1e-7d2a-9f3b-2c4d5e6f7a8b",
  "time": "2026-10-23T08:00:00.000Z",
  "scope": {
    "serverId": "local",
    "projectId": "11111111-1111-1111-1111-000000000001",
    "conversationId": "0199b6ef-1a2b-7c3d-8e4f-5a6b7c8d9e0f"
  },
  "origin": { "kind": "client" },
  "source": "live",
  "channel": "execution",
  "requestId": null,
  "message": {
    "messageId": "chatcmpl-1",
    "role": "assistant",
    "contents": [
      {
        "type": "UsageContent",
        "content": {
          "inputTokenCount": 18100,
          "outputTokenCount": 836,
          "totalTokenCount": 18936,
          "cachedInputTokenCount": 8704,
          "reasoningTokenCount": 750
        }
      }
    ],
    "additionalProperties": { "turnId": "0199b6f0-0000-7000-8000-000000000001", "turnSequence": 12 }
  }
}
```

- request 事件示例：

```json
{
  "kind": "request",
  "id": "0199b6f0-7b00-7d2a-9f3b-2c4d5e6f7a8b",
  "time": "2026-10-23T07:59:58.000Z",
  "scope": {
    "serverId": "local",
    "projectId": "11111111-1111-1111-1111-000000000001",
    "conversationId": "0199b6ef-1a2b-7c3d-8e4f-5a6b7c8d9e0f"
  },
  "origin": { "kind": "client" },
  "channel": "execution",
  "request": {
    "method": "DispatchCommand",
    "args": [
      {
        "type": "ExecCommand",
        "conversationId": "0199b6ef-1a2b-7c3d-8e4f-5a6b7c8d9e0f",
        "agentId": "0199a000-0000-7000-8000-000000000002",
        "agentType": 0,
        "turnId": "0199b6f0-0000-7000-8000-000000000001",
        "stream": true,
        "input": {
          "messageId": "0199b6f0-0000-7000-8000-000000000003",
          "contents": [{ "type": "TextContent", "content": "1+1=?" }]
        }
      }
    ]
  }
}
```

### Extension API（`@agw/extension-sdk`）

- `api.chat.observe(filter, listener): Promise<Disposable>`，需要 `chat.observe`，见 8.2 节。
- `api.execution.createSession(options)` 返回的会话增加 `readOnly: boolean`，参数 id 必须是小写 UUID。
- `onMessage` 接收自己连接及这台 Desktop 其他会话收到的同一对话实时消息。
- 只读对话上的 `send`、`interrupt`、`respond`、`setPermissionMode` 返回 `ERR_CONVERSATION_READ_ONLY`。

### Bridge 方法

- `chat.observe`：
  - 方向：extension → 宿主；
  - 权限：`chat.observe`；
  - 参数：`{ subscriptionId: string; filter: ChatEventFilter }`；
  - 返回：null；
  - 错误：`ERR_FRAME_CLOSING`、`ERR_PERMISSION_DENIED`、`ERR_INVALID_ARGUMENT`、`ERR_TOO_MANY_SUBSCRIPTIONS`。
- `chat.unobserve`：
  - 方向：extension → 宿主；
  - 权限：不需要，只能取消当前连接上的订阅；
  - 参数：`{ subscriptionId: string }`；
  - 返回：null。
- `chat.event`（事件）：
  - 方向：宿主 → extension；
  - 数据：`{ subscriptionId: string; event: ChatEvent }`。

## 测试计划

### 单元测试

**服务端**

- `tests/Agw.Agents.Tests/AgentTelemetryUsageTests.cs`：在 Turn 的 `ExecutionScope` 中运行时，写入的用量带该 Turn 的 id；没有 `ExecutionScope` 时为 null；审批的每一轮各写入一条，都带同一个 Turn id；后台 Agent（`BackgroundAgentScopeAgent`）的用量带启动它的 Turn 的 id。
- `tests/Agw.Agents.Tests/AgentTurnSummaryServiceTests.cs`：summary 的用量带当前 Turn 的 id。
- `tests/Agw.Projects.Tests/AgentUsageRecorderTests.cs`：`turnId` 写入 `TurnId`。
- `tests/Agw.Projects.Tests/ProjectConversationAppServiceTests.cs`：
  - `Running` 与 `Accepted` 的 Turn 的用量不计入 `usage`，Turn id 出现在 `unsettledTurnIds` 中；
  - Turn 为终态且已经到达结算时间时计入，不出现在 `unsettledTurnIds` 中；
  - Turn 为终态但 `usage_settles_at` 晚于当前时间（用 `TestTimeProvider` 控制时间）时仍在 `unsettledTurnIds` 中，时间过去之后计入；
  - `TurnId` 为 null 的记录计入；
  - `TurnId` 非空但没有对应 Turn 行的记录计入，保留全部计数字段，不将该 id 加入 `unsettledTurnIds`；
  - 通过实际清空记录和 checkpoint 恢复（InProcess 与 Distributed）删除 Turn，确认 `agent_usage` 及其非空 `turn_id` 保留，随后读取详情的累计用量保持不变，已删除 Turn 的 id 不在 `unsettledTurnIds` 中。checkpoint 场景的用量属于边界之后被删除的 Turn，在恢复分支产生新用量之前读取；SQLite 与 PostgreSQL 都运行这些用例；
  - 第一次用量查询读到未结算分组之后、第二次查询之前提交 Turn 删除，本次响应仍将该 id 列为未结算；下一次详情读取将保留的用量计入累计值；
  - 还没有用量记录的运行中 Turn 也出现在 `unsettledTurnIds` 中；
  - 其他对话的 Turn 与用量不影响结果。
- `Agw.Projects.Tests` 中 `ConversationTurnStore` 的测试：`FinishAsync` 写入 `usage_settles_at`；`SettleUsageAsync` 只在 Turn 为终态、结算时间晚于当前时间时改为当前时间，非终态与已经结算的 Turn 不修改。
- `Agw.Agents.Tests` 中 Durable 执行的测试：
  - `InterruptAsync`：中断前有未过期的租约时，`usage_settles_at` 为结束时间加 `LeaseSeconds`；排队中或等待人工输入时为结束时间；
  - `InterruptAsync`：中断前有未过期的租约时，`interrupted_lease_epoch` 为这份租约的 `LeaseEpoch`，没有租约时为 null；
  - `RunLeasedSegmentAsync`：被中断的 Segment 退出后确认结算并清空 `interrupted_lease_epoch`，退出时 telemetry 写入的用量带该 Turn 的 id，并且写在结算之前；
  - 接管后中断，旧 worker 先退出：worker A 的租约过期，worker B 接管（`LeaseEpoch` 加一）后被中断；A 先退出时 `usage_settles_at` 不变，该 Turn 仍在 `unsettledTurnIds` 中；B 退出后结算；B 没有退出时，到 `结束时间 + LeaseSeconds` 之后结算；
  - 跨实例中断（两个 coordinator 共用一个 PostgreSQL 数据库）：实例 A 运行 Segment，实例 B 中断；B 提交结束事件之后读取对话详情，该 Turn 在 `unsettledTurnIds` 中，`usage` 不含它；A 续租失败、退出并写入用量之后，详情不再列出该 Turn，`usage` 包含 A 写入的用量；A 在中断前已经停止时，到 `结束时间 + LeaseSeconds` 之后该 Turn 计入。
- `GetUsageAsync` 的分组与关联查询、跨实例中断在 PostgreSQL 测试中运行（`Category=Postgres`，测试名登记到 `.github/scripts/verify-postgres-tests.py`）。
- 运行 `tests/Agw.Architecture.Tests`（实体增加字段）。

开放状态与 Turn 删除路径（10.1 节）：

- `AgentflowRuntimeCheckpointTests.cs`（`AgentflowTurnExecutorTests`）与 `AgentflowCheckpointStoreTests` 通过实际 Turn 受理与 checkpoint 记录流程，覆盖有输入、没有输入的 Turn：断言来源 Turn 的 `FirstSequence <= BoundarySequence`，InProcess 恢复删除后续 Turn、保留来源 Turn 与所选 checkpoint；恢复准备提交后，即使新 Turn 受理失败，创建方为 `host`、`generation` 为 0 的对话仍有 Turn，`openToExtensions` 为 false，其他 extension 的写入被拒绝。
- Distributed 恢复在同一事务内删除后续 Turn 并受理恢复 Turn；恢复受理或保存失败时，历史、Turn、checkpoint 与 Durable 登记一同回滚。SQLite 与 PostgreSQL 都验证成功提交后至少存在一条 Turn；PostgreSQL 使用另一连接验证不会读到已提交的无 Turn 中间状态。
- `DurableTurnUpgradeTests` 验证旧 Turn 替换与活动 Turn 写入一同提交，提交后至少保留对应活动 Turn，创建方与 `generation` 保持不变。
- `ProjectDeletionCoordinatorTests` 验证清空记录在同一事务中增加 `generation` 并删除 Turn、保留创建方；删除失败时两者一同回滚。清空后读取详情验证上述累计用量规则。删除对话、项目内全部对话与 Project 时，Turn 与对应对话行一同删除。
- 删除后使用同一 `conversationId` 重新创建的场景由 `StartTurnAsync_ConversationRecreatedByAnotherExtension_ThrowsConversationReadOnlyWithCachedTask` 覆盖，确认旧连接的 `_resolvedTask` 不能替代本次权限检查。

创建方与访问规则（`Agw.Shared.Tests`、`Agw.Projects.Tests`）：

- `ProjectConversationBehaviorTests`：
  - `EnsureWritableBy_RequesterWithoutExtension_AllowsAnyConversation`（Theory：创建方为 `host` 与 `sample`，有无已受理 Turn）；
  - `EnsureWritableBy_CreatorExtension_Allows`；
  - `EnsureWritableBy_OpenConversation_AllowsAnyExtension`；
  - `EnsureWritableBy_HostConversationWithAcceptedTurn_ThrowsConversationReadOnly`；
  - `EnsureWritableBy_ClearedHostConversation_ThrowsConversationReadOnly`（generation 为 1、没有 Turn）；
  - `EnsureWritableBy_OtherExtension_ThrowsConversationReadOnly`，同时断言 403_0008 与 Forbidden；
  - `IsCreatedBy_RequesterExtension_ComparesWithCreatorOrdinally`（Theory：`sample`、`Sample`、`other`）；
  - `RecordCreatorIfOpen_ExtensionOnOpenConversation_RecordsCreator`；
  - `RecordCreatorIfOpen_HostOrNotOpen_KeepsCreator`（Theory）。
- `TaskProjectionModelTests.ProjectConversation_ExtensionId_MapsToRequiredColumnWithHostDefault`：长度 64、不可为空、默认值 `host`。
- `InitialMigrationTests`：脚本与表结构包含新增字段及数据填充。
- `Infrastructure/ConversationCreatorExtensionMigrationTests` 参照 `TurnUsageSettlementMigrationTests`，验证字段与已有数据：
  - `Upgrade_Sqlite_ExistingConversationsGetHostCreator`；
  - `Upgrade_Postgres_AddsRequiredColumnWithHostDefault`；
  - `Downgrade_Sqlite_DropsColumnWithoutRebuildingTables`，脚本没有 `ef_temp_` 与 `FOREIGN KEY`。
- `ProjectConversationAccessFacadeTests`：
  - `EnsureWritableAsync_DraftConversation_Allows`；
  - `EnsureWritableAsync_OwnConversation_Allows`；
  - `EnsureWritableAsync_RequesterWithoutExtension_Allows`；
  - `EnsureWritableAsync_OpenConversation_Allows`；
  - `EnsureWritableAsync_HostConversationWithAcceptedTurn_ThrowsConversationReadOnly`；
  - `EnsureWritableAsync_ClearedHostConversation_ThrowsConversationReadOnly`；
  - `EnsureWritableAsync_ClearInterleavesWithAccessQuery_ThrowsConversationReadOnly`：SQLite 与 PostgreSQL 各用两个实际连接，初始对话为 `host`、`generation == 0` 且有已完成 Turn；分别控制清空记录在权限查询的快照建立之前、快照建立之后提交，两种情况下都以 `ConversationReadOnly` 拒绝。SQLite 使用项目目录内的 WAL 测试数据库，使清空可以在读取期间提交；确认权限检查只发出一条含 `EXISTS` 的查询，并以清空提交后的数据为基准，确认权限检查没有额外写入标题、更新时间、创建方或历史；
  - `EnsureWritableAsync_OtherExtensionConversation_ThrowsConversationReadOnly`；
  - `EnsureWritableAsync_ConversationOfAnotherUser_AllowsLikeDraft`。
- `TaskAppServiceTests` 使用实际 SQLite：
  - `ResolveTaskAsync_NewConversationForExtension_RecordsCreatorExtension`；
  - `ResolveTaskAsync_NewConversationWithoutExtension_RecordsHostCreator`；
  - `ResolveTaskAsync_HostContinuesExtensionConversation_KeepsCreatorExtension`；
  - `ResolveTaskAsync_ExtensionContinuesOwnConversation_ReusesConversation`；
  - `ResolveTaskAsync_ExtensionOnHostConversationWithAcceptedTurn_ThrowsConversationReadOnlyWithoutWrites`，历史、标题、UpdateTime、UpdateBy 保持不变；
  - `ResolveTaskAsync_ExtensionOnOtherExtensionConversation_ThrowsConversationReadOnlyWithoutWrites`；
  - `ResolveTaskAsync_ExtensionOnOpenConversation_RecordsCreatorExtension`；
  - `ResolveTaskAsync_ExtensionOnClearedHostConversation_ThrowsConversationReadOnly`；
  - `ResolveTaskAsync_ConcurrentInsertOfHostConversationWithAcceptedTurn_ThrowsConversationReadOnlyForExtension`；
  - `ResolveTaskAsync_ConcurrentInsertBySameExtension_ReusesConversation`；
  - `ResolveTaskAsync_OpenConversationClaimedAndAcceptedByOtherExtension_ThrowsConversationReadOnlyWithoutWrites`：A 记录创建方并受理之后，B 被拒绝，B 的历史、标题与更新时间均未写入；
  - `ResolveAsync_RequesterExtension_RecordsCreatorOnNewConversation`，通过 `ProjectTaskFacade`；
  - 现有 `ExecutionTaskRequest` 构造补上 `RequesterExtensionId: null`，包括 `TaskResolutionResetTests`。
- `Infrastructure/ConversationCreatorStoreTests` 使用实际 SQLite 与 PostgreSQL：
  - `ClaimOpenConversationAsync_OpenConversation_RecordsCreator`；
  - `ClaimOpenConversationAsync_SameExtensionAgain_KeepsCreator`；
  - `ClaimOpenConversationAsync_ClaimedByOtherExtension_ThrowsConversationReadOnly`；
  - `ClaimOpenConversationAsync_OtherExtensionAcceptedFirst_ThrowsConversationReadOnlyAndKeepsCreator`：B 先读取开放状态，A 记录创建方并受理，B 再以原 generation 调用并被拒绝，创建方保持为 A；
  - `ClaimOpenConversationAsync_HostTurnAcceptedAfterOpenRead_ThrowsConversationReadOnly`；
  - `ClaimOpenConversationAsync_GenerationChanged_ThrowsConversationSessionConflict`；
  - `Postgres_ConcurrentClaims_RecordExactlyOneCreator`：两个连接同时调用，一个成功，另一个以 `ConversationReadOnly` 失败。
- `ProjectConversationAppServiceTests` 增加：
  - `ListResponsesAsync_ConversationsOfHostExtensionAndOpen_ReturnCreatorAndOpenState`；
  - `GetResponseAsync_ConversationCreatedByExtension_ReturnsExtensionId`；
  - `GetResponseAsync_HostConversationWithoutTurns_IsOpenOnlyBeforeClearing`（generation 0 与 1）。

执行身份与写入检查（`Agw.Agents.Tests`）：

- `TurnPersistenceTestKit` 提供 `CreateConversationAccess()`（实际 `ProjectConversationAccessFacade`）、`CreateProjectTasks()`（实际 `ProjectTaskFacade`、`TaskExecutionAppService` 与 `ConversationCreatorStore`）、`SeedProjectAsync`、`AddTurnAsync`、`ReadConversationAsync`、`CountTurnsAsync`；`SeedConversationAsync` 增加 `string extensionId = ConversationCreators.Host`，`CreateSettings` 增加可选 `extensionId`。`ExecutionCommandHandlerTests.CreateContext`、`ExecutionConnectionTests` 的连接与 DI 测试加入 facade。
- `ExecutionRequestsTests`：
  - `Deserialize_SettingCommand_ReturnsExtensionId`；
  - `Deserialize_SettingCommandWithoutExtensionId_LeavesItNull`；
  - `Equals_WhenExtensionIdDiffers_ReturnsFalse`；
  - `FromCommand_HostExtensionId_ThrowsInvalidParam`（`host`、`HOST`、`Host`）；
  - `FromCommand_InvalidExtensionId_ThrowsInvalidParam`（空字符串、`ab`、`Sample`、`-a1`、`a1-`、`a--b`、`a_b`、` sample`、`sample\n`）；
  - `FromCommand_ExtensionIdLength_AcceptsUpTo64Characters`（64 个字符与 `my-ext-2` 通过，65 个字符拒绝）。
- `ExecutionSettingsTests`：
  - `ExtensionId_IsPreservedAndParticipatesInEquality`；
  - `ExtensionId_SurvivesDerivedSettings`；
  - `DurableSettings_RoundTrip_LeavesExtensionIdOut`：序列化的 `DurableExecutionSettings` 不含 `ExtensionId` 字段与 extension 身份值，恢复的 worker 设置中 `ExtensionId` 为 null。
- `TurnAcceptanceTests`：
  - `AcceptAsync_ExtensionFirstTurnRejected_KeepsCreatorAndAllowsRetry`：Agent 不存在时保留 extension 创建方，同一 extension 重试成功，其他 extension 被拒绝；
  - `AcceptAsync_HostFirstTurnRejected_LeavesOpenConversationForExtension`。
- 新 partial 文件 `ExecutionConnectionWriteAccessTests`（属于 `ExecutionCommandHandlerTests`）：
  - `ApplySettingsAsync_ChangedExtensionId_ReleasesRuntimeAndClearsResolvedTask`；
  - `StartTurnAsync_ExtensionOnDraftConversation_ResolvesWithRequesterAndAcceptsNextTurn`；
  - `StartTurnAsync_ExtensionOnHostConversationWithAcceptedTurn_ThrowsConversationReadOnlyBeforeResolving`，没有解析、runtime 与 Turn 行；
  - `StartTurnAsync_ExtensionOnOtherExtensionConversation_ThrowsConversationReadOnly`；
  - `StartTurnAsync_ExtensionOnOpenConversation_AcceptsTurnAndRecordsCreator`；
  - `StartTurnAsync_HostOnExtensionConversation_AcceptsTurn`；
  - `StartTurnAsync_ConversationRecreatedByAnotherExtension_ThrowsConversationReadOnlyWithCachedTask`；
  - `ResumeCheckpointAsync_ReadOnlyRequester_ThrowsConversationReadOnlyBeforeCheckpointAccess`；
  - `InterruptTurnAsync_ReadOnlyRequester_ThrowsConversationReadOnlyWithoutMessages`；
  - `SubmitHumanDecisionAsync_ReadOnlyRequester_ThrowsConversationReadOnly`；
  - `SetModeAsync_ReadOnlyRequester_ThrowsConversationReadOnly`；
  - `SetPermissionModeAsync_ReadOnlyRequester_ThrowsConversationReadOnlyAndKeepsSettings`；
  - `SetPermissionModeAsync_ClearInterleavesWithAccessCheck_ThrowsConversationReadOnlyAndKeepsSettings`：使用实际 facade 与清空实现控制查询交错，拒绝后 `Settings` 保持不变，也没有写出权限更新消息；
  - `GetAgentflowCheckpointsAsync_ReadOnlyRequester_ReturnsCheckpoints`；
  - 宿主受理 Durable Turn、只读连接成功订阅后：`InterruptTurnAsync_ReadOnlyRequesterAttachedToDurableTurn_ThrowsConversationReadOnlyAndKeepsTurnRunning`、`SubmitHumanDecisionAsync_ReadOnlyRequesterOnDurableTurn_ThrowsConversationReadOnly`、`SetPermissionModeAsync_ReadOnlyRequesterOnDurableTurn_ThrowsConversationReadOnlyAndKeepsSettings`。
- `ExecutionConnectionTests`：
  - `RecoverInProcessExecution_ForeignOrMissingConnection_DoesNotExposeOrInterruptTurn` 增加调用方连接；
  - `RecoverInProcessExecution_InterruptFromOtherExtension_ThrowsConversationReadOnlyAndKeepsTurn`；
  - `RecoverInProcessExecution_InterruptFromHostOrCreator_StopsTurn`（Theory）；
  - `RecoverInProcessExecution_StatusFromOtherExtension_ReportsRunningTurn`；
  - `RecoverInProcessExecution_InterruptFromUnknownCaller_ThrowsInvalidParam`。

**`@agw/execution-core`（`src/chat-events.test.ts`）**

- 空条件匹配 request、response、message 三种事件。
- `kinds`、`channels`、`origins`、`serverId`、`projectId`、`conversationId` 分别只放行对应的事件；多个字段同时给出时，只有全部满足的事件匹配。
- `scope` 中为 null 的字段不满足给出的同名条件。
- `roles` 不区分大小写；没有 `role` 的消息不匹配。
- `messageTypes`：`"result"` 匹配 `additionalProperties.type` 为 `result` 的消息；`null` 匹配没有 `type` 的消息，不匹配有 `type` 的消息。
- `contentTypes`：消息含任一列出的 content 类型时匹配。
- `sources`：`["live"]` 只匹配推送消息，`["history"]` 只匹配历史消息，两者都给出时两种消息都匹配。
- 给出 `sources`、`roles`、`messageTypes` 或 `contentTypes` 时，request 与 response 事件不匹配。
- `validateChatEventFilter`：合法条件返回 null；非对象、未知字段、空数组、未定义的 `kinds`/`channels`/`origins`/`sources` 值、空字符串、`messageTypes` 中既非字符串也非 null 的值，分别返回问题描述。
- `snapshotChatEvent`：副本与原事件内容相同；副本及其中的嵌套对象、数组都已冻结；之后修改原事件不影响副本；值为 `undefined` 的字段不在副本中。
- `src/protocol.test.ts`：`buildSettingCommand` 只在给出 `extensionId` 时写入该字段。

**`@agw/chat-runtime`**

`src/chat-http-client.test.ts` 的 HTTP 部分用 `@agw/test-harness` 的 `startApiServer` 在本机启动 HTTP 服务，`createBearerApiClient` 作为 `api`，请求经过网络发出：

- `getConversation` 依次派发 request 与 response 事件：`scope`、`origin`（`client`）、`channel`（`http`）、`request.method`、`request.path`（路径模板）、`request.params` 与 `value`（解包后的响应）都正确；返回值与 `getProjectConversationDetails` 相同。
- `getConversationMessages` 在 response 事件之后按 `items` 顺序派发 `source` 为 `history`、`channel` 为 `http` 的 message 事件，`requestId` 为该次 request 事件的 `id`；`turns` 中的输入与 Result 不产生 message 事件。
- `getConversationHistory` 读取两页历史：每页各自产生 request、response 与 message 事件，返回全部消息（替代 `projects-core.test.ts` 中 `getProjectConversationHistory` 的用例）。
- 以 extension `origin` 调用各 HTTP 方法时，request、response 与 history 消息事件都带该发起方；`getConversationHistory` 的每个请求沿用同一值。
- `getConversationTurnInputs` 分页读取时每次请求都有事件，请求带传入 client 的 Bearer 头。
- 测试服务返回 404 的 ApiResult：response 事件为 `ok: false`，`error.status` 为 404，`error.body` 为 ApiResult；`clearConversationRecords` 返回 false。
- 测试服务已关闭：response 事件的 `error.status` 与 `error.body` 为 null，方法以 `ApiTransportError` 拒绝。
- 订阅方收到的事件已冻结，修改时抛出 `TypeError`；方法返回给调用方的对象可以修改，修改不影响已派发的事件。
- 取消订阅后不再收到事件；派发过程中被取消的订阅不收到当前事件，派发过程中新增的订阅从下一个事件开始。
- request 事件的订阅函数抛出异常时，方法以该异常拒绝，测试服务没有收到请求。
- 订阅函数的第二个参数为请求发出时 `identity()` 的值，`baseUrl` 末尾的 `/` 被去掉；请求进行中 `identity()` 的返回值改变时，response 与 history 消息事件仍为发出时的值。
- `subscribe` 的条件不合法时抛出。

`src/chat-http-client.test.ts` 的执行通道部分沿用 `execution-hub.test.ts` 替换 `HubConnectionBuilder.prototype.build` 的方式：

- `createExecutionSession` 后 `configure`：依次派发 `GetExecutionProvider` 与 `DispatchCommand`（`SettingCommand`）的 request、response 事件，`scope` 为传入的作用范围，`DispatchCommand` 的 `value` 为 null。
- hub 调用失败：response 事件为 `ok: false`，`error.message` 为 hub 错误信息，调用方收到原异常。
- `ReceiveMessage`：`handlers.onMessage` 先收到消息，之后派发 `source` 为 `live` 的 message 事件；事件中的消息没有客户端设置的顶层 `streamingScopeId`；`dispose` 之后收到的消息不派发。
- 按来源分别订阅：先读取一页历史，再收到 `ReceiveMessage` 推送；`{ sources: ["live"] }` 只收到推送消息，`{ sources: ["history"] }` 只收到历史消息，`{ kinds: ["message"] }` 两种都收到。
- `options.origin` 为 extension 时事件的 `origin` 为该值；`options.runtime` 的 `webSocket` 用于建立连接。
- 执行通道事件的第二个参数为创建会话时 `runtime` 的 `baseUrl`（规范化后）与 `token`；之后全局执行配置改变，已创建会话的事件仍为原来的值。
- 重连后恢复设置与 durable 订阅：`SettingCommand` 与 `SubscribeTurnCommand` 产生 request 事件。
- extension 会话在每个 `SettingCommand` 中发送 `extensionId`，包括 `hubs[0].reconnect()` 之后；宿主会话始终省略该字段。
- Distributed 模式下，`handlers.onMessage` 与 `observer.message` 都成功后，才更新内存 cursor 与 attachment store；没有 observer 时以 handler 成功为准。
- Distributed 模式下，序号 N 的订阅函数抛出异常，随后收到 N+1：N+1 不交给 handler 或 observer，attachment store 保留 N 之前的 cursor；触发自动重连，断言实际发出的 `SubscribeTurnCommand.cursor` 同样是 N 之前的位置。订阅函数恢复正常后，补发 N、N+1，依次处理并提交 cursor。手动重连与重新创建会话的刷新场景检查相同的请求参数；首条消息失败时 cursor 为 null。
- Distributed 模式下，`handlers.onMessage` 抛出异常：observer 不收到这条消息，内存与存储中的 cursor 不变，恢复订阅后从失败位置继续。
- Distributed 模式下，结束消息的订阅函数抛出异常：attachment 保留，`hasRunningTurn()` 为 true，等待结束的 Promise 尚未完成，新执行不能覆盖恢复状态；补发结束消息并处理成功后，才清除 attachment 并完成等待。恢复期间再次处理失败时仍保留原 cursor；服务端明确返回 `DurableExecutionNotFound` 时按现有流程清理。

`src/conversation-token-usage.test.ts` 直接使用 `ChatHttpClient`：详情响应来自 `startApiServer`，每次请求返回测试设定的 `usage` 与 `unsettledTurnIds`；推送消息来自执行会话的 `ReceiveMessage`（连接替换方式同上）：

- 采用详情响应中的已结算用量；未结算 Turn 的 `UsageContent` 计入；收到该 Turn 的结束消息后发出同步请求，响应到达后显示响应中的值，该 Turn 的推送用量被删除。
- Turn 运行中再次读取详情，响应把该 Turn 列在 `unsettledTurnIds` 中：已经推送的用量继续计入。
- 刷新场景：新的 store 先收到已包含 Turn R 的基线（R 不在 `unsettledTurnIds` 中），再收到 R 补发的 `UsageContent` 与结束消息：显示值一直等于基线，同步响应之后不变。
- 补发包含 R 的开始消息：R 在补发期间计入，同步响应之后被删除，显示响应中的值。
- `turnId` 与 `turnSequence` 相同的 `UsageContent` 只计入一次；一条消息中的多个 `UsageContent` 全部计入。
- Distributed 模式下，分别在用量订阅之前、之后注册会抛出异常的订阅：序号 N 失败后收到 N+1，记录的最大序号不能越过 N；订阅恢复正常后重新连接，补发 N、N+1 后两条用量各计入一次。用量状态已保存、cursor 尚未提交时重新创建会话与 store，补发同一消息也只计入一次。
- `resultOnly`：Turn 没有推送 `UsageContent`，结束后的同步把用量更新为响应中的值。
- summary：同步响应的 `usage` 比推送合计多出 summary 的用量，显示响应中的值。
- 两个详情请求的响应以相反的顺序到达：采用请求序号更大的响应。
- 详情请求失败、同步请求失败时用量不变。
- 跨 Server 后台完成：会话按连接 A 创建；`identity()` 改为连接 B 之后，会话收到结束消息：A、B 两个测试服务都没有收到详情请求，记录不变；`identity()` 改回 A 之后读取详情，记录更新。
- Distributed 中断：结束后的同步响应仍列出该 Turn 时，推送用量继续计入，并在 1、2、4、8 秒后各同步一次（用 `t.mock.timers` 控制时间），之后每 10 秒一次；响应不再列出该 Turn 时删除记录并停止；某次同步失败时停止。
- 运行中刷新、继续等待审批：store A 与 Distributed 执行会话（连接替换方式）收到 Turn T 的 `UsageContent`（`turnSequence` 为 3）后，写入 storage 与 durable cursor；用同一个 storage 与 attachment store 新建 ChatHttpClient、执行会话与 store B，模拟刷新：基线把 T 列为未结算，显示值与刷新前相同；恢复订阅时补发的序号 3 被忽略，审批之后的序号 5 计入；T 结束、同步之后，storage 中的记录被移除。
- InProcess 模式下刷新：恢复的记录在 `RecoverInProcessExecution` 返回 false 之后触发同步，同步响应到达后被删除。
- InProcess 模式下，开启 `resultOnly` 时刷新、在第一条 `UsageContent` 到达之前刷新：store 中没有推送用量，基线列出该 Turn；`RecoverInProcessExecution` 返回 false 之后发出同步，面板显示响应中的值。
- `FindInProcessExecution` 返回 null：基线没有未结算 Turn、也没有未结算记录时不发请求；基线列出未结算 Turn 时发出一次；确认先到、基线后到时，采用基线之后发出；同步得到的基线不再触发请求。
- storage 的读写抛出异常时统计照常进行。
- 缺少 `turnId` 或 `turnSequence` 的 `UsageContent`：订阅函数抛出异常。
- 新对话没有基线：全部 Turn 的推送用量计入。
- 不同对话、不同 `serverId` 的用量互不影响；history 消息中带 `UsageContent` 时不计入。
- 没有记录的对话返回 `EMPTY_TOKEN_USAGE`；用量变化时调用 `subscribe` 的函数，未变化时 `get` 返回同一个对象；`dispose` 之后不再变化。

`src/execution-session-manager.test.ts`：

- 默认工厂经 `chatHttpClient` 创建会话：`attach` 后的 `configure` 产生以会话键为作用范围的事件。
- Distributed 模式下，结束消息的订阅函数抛出异常时，等待发送的条目保留且不发送；自动重连后补发结束消息并处理成功，队列只继续发送下一条。补发的结束消息已经被 manager 处理过时，同样会再次检查队列。

`src/execution-hub.test.ts`：进程内停止被拒绝（`HubException: 4030008`）后继续查询；`t.mock.timers.tick(1000)` 后原 Turn 结束时会话恢复空闲。

**`@agw/projects-core` 与 `@agw/projects`**

- `getConversationTurnInputs` 使用传入的 client：请求带该 client 的 Bearer 头。
- `projects-core.test.ts` 与 `projects/src/services/task-client.test.ts` 中对 `ConversationDetails.usage` 的断言随删除修改。
- 列表与详情映射 `extensionId`、`openToExtensions`：覆盖 `host`、extension id 与字段缺少的情况，缺少时分别为 null、false。
- `conversation-list.test.ts`：`host`、`sample` 与缺少创建方的对话，标题前分别显示 `lucide-message-circle-text`（`Created by Agw`）、`lucide-puzzle`（`Created by extension "sample"`）与空白位置。

**`@agw/chat`**

- `chat-workspace-hydration.test.ts`：模块替换改为由 `@agw/chat-runtime` 提供 `chatHttpClient.getConversation` 与 `chatHttpClient.getConversationMessages`；打开对话时以 `{ serverId, projectId, conversationId }` 调用；`ChatSessionSeed` 不含 `usage`。
- 新增 `chat-token-usage.test.ts`（`setupDomEnvironment`，`configureApiRuntime` 指向 `startApiServer`，执行会话沿用连接替换方式）：
  - 用 `chatHttpClient.getConversation` 读取带 `usage` 的详情后渲染 `Chat`，`ChatAside` 显示该基线；
  - 该对话的执行会话收到 `UsageContent` 后，`ChatAside` 显示基线加推送用量；收到结束消息、同步响应到达后，显示响应中的值；
  - 恢复 Agentflow checkpoint 后，`ChatAside` 的值不变。
- `chat-extensions.test.ts` 与 `chat-workspace-hydration.test.ts` 的 `sessionSeed` 删除 `usage`。

**`@agw/chat-native`**

- `native-execution-session.test.ts`：用 `ChatHttpClient` 与作用范围构造 `NativeExecutionSession`，原有用例的断言不变；新增用例：会话收到的消息派发 message 事件，作用范围为传入的 `serverId`、`projectId`、`conversationId`。
- Mobile `history-screen.test.tsx`：宿主、extension 与缺少创建方的三种对话，`Created by Agw`、`Created by extension "sample"` 各出现一次；缺少创建方的对话没有创建方标签，列表中没有其他创建方标签。

**Desktop**

- `desktop/src/shared/extension-manifest.test.ts`：`chat.observe` 是合法权限。
- 同一 manifest 测试覆盖 `host` 保留名称：`id` 为 `host` 校验失败，`host-tools`、`my-host`、`hosts` 合法；`name` 为 `host`、`Host`、`" HOST "` 校验失败且错误路径为 `name`，`Host Tools`、`My host` 合法；生成的 JSON Schema 对全部样例给出相同结果。
- `extension-bridge.test.ts`（使用实际的 broker、SDK 与 `MessageChannel`；broker 与 `ExecutionService` 的 `chat` 是 `api` 指向测试 API 服务的同一个 `ChatHttpClient`；需要执行会话的用例沿用 `execution-hub.test.ts` 的连接替换方式）：
  - 没有 `chat.observe` 时 `api.chat.observe` 以 `ERR_PERMISSION_DENIED` 拒绝；
  - 条件不合法、`subscriptionId` 重复时为 `ERR_INVALID_ARGUMENT`；
  - 订阅 `{ sources: ["history"] }` 后，宿主调用 `getConversationMessages`，extension 按顺序收到每条历史消息，没有收到 request 与 response 事件；同时存在的 `{ sources: ["live"] }` 订阅没有收到这些消息；
  - 不属于当前连接的事件不发送：profile id 不同的事件；同一 profile 修改 `baseUrl` 之后，修改之前创建的执行会话的事件（修改之后创建的会话的事件照常发送）；同一 profile 更换 token 之后的同样情形；修改之前发出、之后才返回的 HTTP 请求的 response 与 history 消息事件；
  - `SettingCommand` 的 request 与 response 事件中没有 `environmentVariables`，其他字段不变；
  - `dispose` 之后不再收到事件，重复 `dispose` 不报错；
  - 第 17 个订阅为 `ERR_TOO_MANY_SUBSCRIPTIONS`，取消一个之后可以再订阅；
  - `broker.disconnect` 之后，该连接的订阅从 ChatHttpClient 中取消（测试包装 `subscribe` 统计未取消的订阅数）；
  - frame 正在关闭时为 `ERR_FRAME_CLOSING`；
  - extension 用 `api.execution.createSession` 创建的会话，其 hub 调用事件的 `origin` 为 `{ kind: "extension", extensionId: "sample" }`，`scope.serverId` 为当前 profile id。
  - harness 的 `deliver` 接到 `broker.deliverExecution`。会话用例提供详情路由，供 `createSession` 查询创建方与开放状态；测试 API 服务对未知路由返回 404/4040000，这个错误会使 `createSession` 失败，只有 404/4040007 按草稿处理。
  - 宿主创建且有已受理 Turn 的对话上，`readOnly` 为 true；`send`、`interrupt`、`respond`、`setPermissionMode` 把 `HubException: 4030008` 映射为 `ERR_CONVERSATION_READ_ONLY`，覆盖 InProcess 与 Distributed（`SubscribeTurnCommand` 返回 4040011）。
  - 自己的对话、开放对话、草稿（404/4040007）与新对话的 `readOnly` 为 false；新对话不发详情请求。
  - 第二个 frame（`extensionId: "other"`）看到 sample 的对话为只读。
  - 详情返回 500 时 `createSession` 失败，hub 连接尚未建立。
  - `projectId`、`conversationId` 为 `.`、`..` 或其他非小写 UUID 时返回 `ERR_INVALID_ARGUMENT`，测试服务没有收到请求，`chat.observe` 没有收到事件，hub 连接尚未建立。
  - 详情请求带 extension 的 `origin`，`SettingCommand` 带 `extensionId: "sample"`。
  - 宿主与其他 extension 的会话在同一对话收到消息时，当前 extension 的 `onMessage` 收到转发；同一 extension、其他对话、不同连接身份的消息跳过；`dispose` 之后停止转发。

### 集成测试

`pnpm --filter @agw/desktop test:extensions` 启动 SQLite Standalone Host 与回显服务，Desktop 通过网络连接它们：

- `scripts/test-extensions.mjs` 的回显服务在流式响应的 `data: [DONE]` 之前写出用量 chunk：`{ id, object: "chat.completion.chunk", created, model, choices: [], usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 } }`。`execution()` 步骤的回复文字断言不变。
- `test-fixtures/extensions-src/sample/manifest.json` 的 `permissions` 增加 `chat.observe`；`enableSample` 检查批准弹窗列出它。
- 新步骤 `chatObserve()` 在 `chatMarkdown()` 之后运行，前面步骤对已打开对话内容的断言不受影响：
  1. sample 主视图建立四个订阅，把收到的事件分别保存到 `window`：`{ kinds: ["response"], channels: ["http"] }`、`{ sources: ["live"], contentTypes: ["UsageContent"] }`、`{ kinds: ["request"], channels: ["execution"] }`、`{ sources: ["history"] }`；
  2. 宿主先开始一个新对话，再从对话列表打开 `execution()` 创建的对话，使详情请求与历史请求发生在订阅之后；sample 收到 `request.path` 为对话详情路径的 response 事件，记下其中的 `usage.totalTokenCount` 为基线；`{ sources: ["history"] }` 订阅收到该对话的历史消息，其中有用户输入 `integration hello`，`UsageContent` 订阅此时没有收到事件；
  3. `ChatAside` 只在对话区域宽度不小于 64rem 时显示：宽度不足时先用 sample 的 `api.workspace.setSidebarVisible(false)` 隐藏 Left Sidebar，测试确认它可见（`getBoundingClientRect` 与 `elementFromPoint`）后，再检查 Total 行为 `formatTokenCount(基线)`；
  4. 在对话输入框中发送 `observe hello`，等待回复 `Echo: observe hello` 出现；
  5. sample 收到 `request.args[0].type` 为 `ExecCommand`、`origin.kind` 为 `client`、`scope.conversationId` 为该对话的 request 事件；收到的 `UsageContent` 合计 `totalTokenCount` 为 2；`ChatAside` 的 Total 为 `formatTokenCount(基线 + 2)`；
  6. 测试脚本另建一个启用 summary 的 Agent（`enableSummary` 为 true，`summaryModelProviderId` 为回显服务的 Provider）。sample 用 `api.execution.createSession({ projectId, conversationId: 该对话 })` 以这个 Agent 运行一轮：sample 收到 `origin` 为 `{ kind: "extension", extensionId: "sample" }` 的 request 与 `UsageContent` 事件，`UsageContent` 合计为 2；Turn 结束、同步完成后，`ChatAside` 的 Total 再增加 4，即 Agent 推送的 2 加上没有推送的 summary 的 2（回显服务的非流式响应返回 `total_tokens: 2`）。
- `test-fixtures/extensions-src/no-background/manifest.json` 增加 `"permissions": ["execution"]` 并更新说明，作为第二个可执行的 extension。
- `chatObserve()` 之后验证宿主对话的只读规则与实时消息：
  1. 宿主开始新对话，通过 `#chat-target-select` 选择 Echo，发送 `host hello` 并等待 Turn 结束。新对话默认采用项目最近的选择或默认 Agent，因此这一步显式选择 Echo。
  2. `hostApi` 列表中该对话的 `extensionId` 为 `host`、`openToExtensions` 为 false；sample 的 `integration hello` 对话经过宿主继续聊天后，创建方仍为 `sample`。
  3. sample 在宿主对话创建会话，`readOnly` 为 true；`send`、`interrupt`、`setPermissionMode`、`respond` 都返回 `ERR_CONVERSATION_READ_ONLY`。
  4. 宿主发送 `host again`，sample 的 `onMessage` 收到 `Echo: host again`。
  5. 该对话的 Turn 只有宿主发起的两轮。
  6. 调用 `showMarkdownReply()` 恢复此前打开的对话。
- `failureModes()` 之后使用 no-background 视图验证其他 extension 的只读规则：
  1. 在 sample 对话创建会话，`readOnly` 为 true，`send` 失败。
  2. 创建新对话并发送消息，列表中的创建方为 `no-background`。
  3. sample 看到这个对话为只读。

### 回归测试

- `dotnet build Agw.slnx`、`dotnet test Agw.slnx`，以及 `docs/1.Development.md` 中的 PostgreSQL 测试命令。
- `dotnet test tests/Agw.Architecture.Tests`、`dotnet test tests/Agw.Shared.Tests`。
- `pnpm test`（先运行 `test:boundaries`，再运行每个包的 `typecheck` 与 `test`）、`pnpm lint`、`pnpm fmt:check`、`pnpm build`。
- `pnpm --filter @agw/web test:e2e`。
- `pnpm --filter @agw/mobile test`。

## 实施步骤

每一步先写测试并确认失败，再修改实现；按照依赖顺序完成以下工作。

1. 创建方与规则（第 10、11 节）：增加 `ConversationCreators.Host`、`ConversationReadOnly`、实体字段与配置，扩展 `ProjectConversationBehavior`。检查：`Agw.Shared.Tests`、`ProjectConversationBehaviorTests`、`TaskProjectionModelTests`、`Agw.Architecture.Tests` 通过。
2. Turn 用量（5.1 节）：修改 `AgentUsage`、`ProjectConversationTurn`、`IAgentUsageRecorder`、`AgentUsageRecorder`、`AgentTelemetryMiddleware`、`AgentTurnSummaryService`、`IConversationTurnStore.FinishAsync` 的六个调用位置与 `SettleUsageAsync`，增加 `interrupted_lease_epoch` 并按租约代次确认结算。检查：`dotnet build Agw.slnx`、`dotnet test tests/Agw.Agents.Tests`、`dotnet test tests/Agw.Projects.Tests`、`dotnet test tests/Agw.Architecture.Tests` 通过，覆盖用量、终态与 Durable 结算；PostgreSQL 迁移、查询与跨实例结算分别在第 3、5、7 步完成后验证。
3. 数据库：用户明确要求时，按“数据库设计”为两个 provider 分别生成用量迁移与 `AddConversationCreatorExtension`，SQLite `Down` 直接删除 `extension_id`。检查：`InitialMigrationTests`、创建方与用量迁移测试通过，PostgreSQL 验证使用仓库规定的测试连接。
4. Projects 任务解析（10.3 节）：增加请求方参数、创建方记录与检查、`IConversationCreatorStore` 及其 Infrastructure 实现。检查：`TaskAppServiceTests`、`TaskResolutionResetTests`、`ConversationCreatorStoreTests`（含 PostgreSQL）、架构测试通过。
5. Projects 查询与检查（5.1、10.4、12 节）：实现 `IProjectConversationAccessFacade` 与注册，权限检查用单条 SQL 投影对话与 Turn 存在性；修改 `GetUsageAsync`、`ProjectConversationUsage`、详情与列表投影，保留左连接中没有对应 Turn 行的用量分组。检查：`ProjectConversationAccessFacadeTests`（含清空记录交错用例）、`ProjectConversationAppServiceTests`（含删除 Turn 后的累计用量）、架构测试及 PostgreSQL 查询测试通过。
6. Server 执行身份（第 9 节）：修改 `SettingCommand`、mapper、`ExecutionSettings`，贯通请求方参数。检查：`ExecutionRequestsTests`、`ExecutionSettingsTests` 通过。
7. Server 写入入口（第 11 节）：修改上下文、工厂、受理传递、注册表、连接、hub 与测试工具。检查：`TurnAcceptanceTests`、`ExecutionCommandHandlerTests`、`ExecutionConnectionTests`、`DurableExecutionStoreTests`，以及完整 `Agw.Agents.Tests` 与 PostgreSQL Durable 测试通过；完成“开放状态与 Turn 删除路径”的测试，验证恢复、清空、升级与删除后重新创建都满足第 10.1 节的并发前提。
8. 服务端验证与接口导出：运行 `dotnet build Agw.slnx`、`dotnet test Agw.slnx`；只对改动文件执行 `dotnet csharpier check`。导出 OpenAPI 并运行 `pnpm gen:api`，检查 schema 中创建方、开放状态与 `usage.unsettledTurnIds`。
9. `@agw/execution-core`（第 2、3、4 节）：实现 `chat-events.ts` 与导出，协议增加 `extensionId`。检查：该包 `test`、`typecheck`、`lint` 通过。
10. `@agw/chat-runtime` 执行通道（第 4 节）：增加 `observer`、`extensionId`、`invoke`、设置生成方法与 `getExecutionRuntime`；实现连续 cursor、失败恢复、结束消息成功后的清理、manager 队列检查，以及停止被拒绝后的状态查询。检查：`execution-hub.test.ts`、`execution-session-manager.test.ts` 及包内测试通过。
11. ChatHttpClient（第 1 节）：projects-core 的 `getConversationTurnInputs` 增加 `client`；chat-runtime 增加依赖与子路径导出，运行 `pnpm install`；实现 `ChatHttpClient`、可选 HTTP `origin`、执行身份传递、单例与 manager 默认工厂。检查：`pnpm test:boundaries`、chat-runtime 与 projects-core 测试通过。
12. Token usage（第 5 节）：实现 `ConversationTokenUsageStore`、本地保存与恢复、结算同步、连接身份检查、单例与 hook。检查：`pnpm --filter @agw/chat-runtime test` 与 `pnpm --filter @agw/chat-runtime typecheck` 通过，运行包内全部测试，包括 `conversation-token-usage.test.ts`、`execution-hub.test.ts` 与 `execution-session-manager.test.ts`。
13. Web、Desktop 与 Mobile（第 6、7 节）：接入 ChatHttpClient 与用量 store，修改 `ChatSessionSeed`、两个执行抽屉、`NativeExecutionSession` 与 provider，删除失去调用方的历史入口与 `ConversationDetails.usage`。检查：chat、agents、projects、projects-core、chat-native、mobile 的相关测试与 `typecheck` 通过。
14. Desktop extension（第 8 节）：修改权限、API、SDK、broker、`ExecutionService`、`ExtensionHost` 与 Extensions 页面，实现 manifest 保留名称、UUID 校验、`readOnly`、错误映射与实时消息转发。检查：extension-sdk、desktop 测试、`pnpm typecheck`、边界测试通过。
15. 创建方图标（第 12 节）：更新两个 lucide 依赖、projects-core 映射与 `HOST_EXTENSION_ID`、Web/Desktop 与 Mobile 列表。检查：`pnpm --filter @agw/projects-core test`、projects 与 mobile 测试通过。
16. fixture 与集成场景：增加观察订阅、用量、宿主只读、其他 extension 只读与转发场景。每次运行前按仓库规则取得用户确认，验证 `pnpm --filter @agw/desktop test:extensions` 的 first、restart 阶段。
17. 文档（第 14 节）与回归：更新对应说明，只格式化改动文件，完成“回归测试”中的检查。

## 待办

- **`ConversationController` 的处理方式待定**：
  - 现状：`packages/chat-runtime/src/conversation-controller.ts` 的 `ConversationController` 与 `use-conversation-controller.ts` 的 `useConversationController` 没有调用方。`ConversationController` 直接调用 `new ExecutionSession(handlers, adapter.execution)`，会话不经过 ChatHttpClient；它用 `getMessageTokenUsage` 自行累加 `usage`。
  - 可以选择的处理：删除这两个文件、`conversation-controller.test.ts` 与 `package.json` 中的 `./controller` 导出；或者改为通过 `ChatHttpClient.createExecutionSession` 创建会话，`usage` 改为读取 `ConversationTokenUsageStore`。
  - 处理时一并考虑：子路径 `@agw/chat-runtime/react` 指向 `use-conversation-controller.ts`，该文件还转出 `useWorkSummary`，`@agw/chat` 的 `conversation.tsx` 与 `@agw/chat-native` 的 `conversation-history.tsx` 从这个子路径导入它；`@agw/api` 的 `getMessageTokenUsage` 在本次改动后只剩 `ConversationController` 一个调用方。
