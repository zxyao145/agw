# 对话侧栏的 Agentflow 节点状态 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**目标：** 对话侧栏在 Token usage 下面显示 Agentflow 的全部节点（竖向排列），正在执行的节点高亮；页面重新载入后恢复运行中 Turn 的节点状态。

**架构：** Turn 开始消息带上 `agentflowRevision`；`ExecutionSessionManager` 用 chat-core 的 `AgentflowNodeRunState` 按对话汇总节点状态消息；`ChatAgentflowNodes` 读取对应 revision 的定义并按执行方向排列节点。重新载入后，`ExecutionSession` 调用新的 Hub 方法 `GetAgentflowNodeStatuses` 取得快照（InProcess 读 `TurnBroadcast` 缓冲，Distributed 按新增的 `execution_stream_entry.message_type` 读事件记录）。

**技术栈：** ASP.NET Core / EF Core（.NET 10）、SignalR、xUnit v3；React、`@agw/components/query`（TanStack Query）、node:test + tsx、jsdom、Playwright。

**设计文档：** `docs/designs/2026-11-27-对话侧栏Agentflow节点状态.md`。每个任务都以设计文档对应章节为准，本计划只写设计文档没有确定的实现决定。

## 全局约束

- 工作区中已有大量与本功能无关的未提交改动，不修改、不格式化它们。开始任务 1 之前先运行一次 `dotnet build Agw.slnx` 与 `pnpm typecheck`（在 `src` 下），记录已经存在的失败。
- 注释使用中文与英文两种语言，技术术语保持英文，不写过多注释。
- C#：只用 `DateTimeOffset` 与 `TimeProvider`；预期失败抛出 `AgwException(ErrorCodes.X)`；日志使用结构化模板；不用 `#region`；不写空 `catch`；写法遵循 `docs/human/2.csharp-coding-rules.md`。
- 失败时立即抛出，不加兜底逻辑。
- 测试使用真实实现，不使用 mock、fake 或只为通过测试的写法（node:test 自带的 `t.mock.method` 只用于替换浏览器或 SignalR 构建器这类测试环境入口，与现有测试一致）。C# 测试名为 `Method_Condition_ExpectedResult`。
- 临时文件放在 `.scratch/` 下（已经列在 `.gitignore` 中），不读写 `/tmp`。
- 不用 Git 回滚代码；不用 `sed`、脚本或 heredoc 修改代码。
- 只在用户要求时提交，本计划不包含提交步骤。
- 格式化：`dotnet csharpier format <改动的 .cs 文件>`；客户端在 `src` 下执行 `pnpm fmt`。
- 运行 `dotnet test` 时使用 `env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS`。只运行单个 C# 测试：`dotnet test --project tests/<Project> --filter-method "*<Name>"`；输出 "Zero tests ran" 时，先 `dotnet build tests/<Project>`，再运行 `dotnet tests/<Project>/bin/Debug/net10.0/<Project>.dll -method "*<Name>"`。
- 只运行单个客户端测试文件：在 `src` 下执行 `pnpm --filter <包名> exec tsx --test <相对包目录的文件路径>`。
- 手动启动 Host 时传入 `--environment=Production`、空闲端口（避开 3000、3001、3101、30816、30817）与 `.scratch` 下的 `--AgwDataDir`、`--AgwLogDir`，并移除继承的 `ASPNETCORE_ENVIRONMENT`、`ASPNETCORE_URLS`。
- EF migration 只在用户确认后生成（任务 2 第 6 步）。
- 固定值：
  - 消息属性键 `agentflowRevision`；Hub 方法名 `GetAgentflowNodeStatuses`；状态事件类型 `agentflow-node-snapshot`。
  - 列 `execution_stream_entry.message_type`（text，可空）；索引列 `(turn_id, message_type, turn_sequence)`。
  - 定义查询键 `["agentflow-detail", agentflowId, revision]`，`staleTime: Infinity`；列表查询键 `["agentflows"]`、`["agents"]`。
  - 卡片 `aria-label="Agentflow nodes"`；节点列表容器 `max-h-[min(50vh,28rem)] overflow-y-auto`；高亮行 `aria-current="step"`，正在输出的成员 `aria-current="true"`。
  - 状态图标的 `aria-label`：`Not started`、`Running`、`Waiting`、`Completed`、`Failed`。

## Review Focus

1. 节点很多的流程（40 个节点）：卡片不能把 Todo 卡片推出视口，节点列表限高并可以滚动。任务 14 的 Playwright 用例 `an Agentflow conversation lists its nodes between Token usage and Todo` 检查。
2. 环路中同一个节点再次执行：`completed` 之后收到 `running` 时重新高亮。任务 8 测试 `a completed node that runs again shows running`。
3. 运行中点击 Stop（Turn 以 `interrupted` 结束）：高亮消失，已经完成的节点保留 Completed 图标。任务 14 测试 `an interrupted turn clears highlights and keeps completed nodes`。
4. 很长的节点名称或中文名称：标题单行截断，`title` 属性保留完整名称。任务 13 测试 `a long node name stays on one line and keeps the full name in title`。
5. 定义读取失败（Agentflow 已删除，或 revision 不存在返回 404）：卡片显示错误文字，侧栏其他卡片正常。任务 13 测试 `a failed definition request shows the error`。

---

### 任务 1：Turn 生命周期消息带 agentflowRevision

**文件：**
- 修改：`src/server/Agw.Agents.Execution/Turns/TurnMessageFactory.cs`
- 修改：`src/server/Agw.Agents.Execution/Turns/TurnAcceptanceService.cs`（`CreateExecutionRequest`、`CreateReplayRequest`）
- 修改：`src/server/Agw.Agents.Execution/Agentflows/Checkpoints/AgentflowCheckpointStore.cs`（`AddResumeStartEvent`）
- 修改：`src/server/Agw.Agents.Execution/Runtimes/Durable/DurableExecutionCoordinator.cs`（`CreateEnvelope`）
- 修改：`src/server/Agw.Infrastructure/Agents/DurableTurnUpgrade.cs`（`UpgradeEventsAsync` 中追加和改写的开始消息）
- 测试：新增 `tests/Agw.Agents.Tests/TurnMessageFactoryTests.cs`；修改 `tests/Agw.Agents.Tests/TurnAcceptanceTests.cs`、`tests/Agw.Agents.Tests/AgentflowCheckpointStoreTests.cs`

**接口：**
- 产出：
  - `internal sealed record TurnEnvelope` 使用显式构造函数。参数依次为 turnId、conversationId、agentId、agentType、streamingScopeId、agentflowRevision，Agent 目标传 0。
  - `TurnMessageFactory.AgentflowRevisionKey = "agentflowRevision"`；`AgentType` 为 Agentflow 时，开始与结束消息都写入该键，值为 int。

- [ ] **步骤 1：写失败的测试**
  - `TurnMessageFactoryTests.CreateStarted_AgentflowEnvelope_IncludesAgentflowRevision`：revision 3 的 Agentflow envelope，`CreateStarted` 与 `CreateFinished(envelope, AgwTurnStatus.Completed, 1, null)` 的 `AdditionalProperties[AgentflowRevisionKey]` 都等于 `3`。
  - `TurnMessageFactoryTests.CreateStarted_AgentEnvelope_OmitsAgentflowRevision`：Agent envelope 的开始消息不包含该键。
  - `TurnAcceptanceTests.ExecCommand_RevisionZero_UsesPublishedRevision` 增加断言：`accepted.Start.AdditionalProperties?[TurnMessageFactory.AgentflowRevisionKey]` 等于 `1`；`ExecCommand_ExplicitRevision_UsesIt` 增加断言：等于 `2`。
  - `AgentflowCheckpointStoreTests.DistributedResume_StartEvent_CarriesSourceRevision`：按 `DistributedResume_NestedRevisionPublished_UsesAcceptedSnapshotOnRetry` 的方式记录根 revision 为 1 的 Durable checkpoint，之后 `PublishRevisionAsync(fixture.AgentflowId, revision: 2)`，再恢复；读取 `TurnId == resumeTurnId && TurnSequence == 1` 的事件记录，`DurableExecutionEvents.ToEntry(record).Message.AdditionalProperties![AgentflowRevisionKey]?.ToString()` 等于 `"1"`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Agents.Tests --filter-class "*TurnMessageFactoryTests"`
预期：编译失败，`AgentflowRevisionKey` 不存在。

- [ ] **步骤 3：实现**

`CreateTurnProperties` 按上面的规则写入，revision 来源见设计文档第 4 节。`DurableTurnUpgrade.UpgradeEventsAsync` 根据 manifest 写入完整的开始属性，覆盖新建的开始消息和已有的 `turn-start`、`agw-turn-start` 消息。Agentflow Turn 的开始属性包含 `agentflowRevision`。

- [ ] **步骤 4：运行测试，确认通过**

运行：`env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Agents.Tests --filter-class "*TurnMessageFactoryTests" --filter-class "*TurnAcceptanceTests" --filter-class "*AgentflowCheckpointStoreTests" --filter-class "*TurnAcceptanceWriterSnapshotTests"`
预期：全部通过。

- [ ] **步骤 5：格式化改动的 .cs 文件**

---

### 任务 2：`execution_stream_entry.message_type` 与按类型读取

**文件：**
- 修改：`src/server/Agw.Data/Entities/Executions/DurableExecutionEventRecord.cs`、`DurableExecutionEventRecordConfiguration.cs`
- 修改：`src/server/Agw.Agents.Execution/Persistence/Durable/DurableExecutionEvents.cs`
- 修改：`src/server/Agw.Agents.Execution/Agentflows/Checkpoints/AgentflowCheckpointStore.cs`（`AddResumeStartEvent`）
- 修改：`src/server/Agw.Infrastructure/Agents/DurableTurnUpgrade.cs`（`Append` 与 `UpgradeEventsAsync` 的逐条循环）
- 新增（生成）：`src/server/Agw.Migrations.Sqlite/Migrations/*_AddExecutionStreamEntryMessageType.cs`、`src/server/Agw.Migrations.Postgres/Migrations/*_AddExecutionStreamEntryMessageType.cs` 及各自的 Designer 与 ModelSnapshot 变更
- 测试：`tests/Agw.Agents.Tests/TurnAcceptanceTests.cs` 改为 `partial`；新增 `tests/Agw.Agents.Tests/TurnAcceptanceTests.EventTypes.cs`；修改 `tests/Agw.Agents.Tests/AgentflowCheckpointStoreTests.cs`

**接口：**
- 消费：任务 1 的 `TurnMessageFactory`。
- 产出：
  - `DurableExecutionEventRecord.MessageType`：`public string? MessageType { get; set; }`，带中英文 summary。
  - `public static Task<IReadOnlyList<TurnBroadcastEntry>> DurableExecutionEvents.ReadByTypesAsync(IAgentsDbContext dbContext, Guid turnId, IReadOnlyCollection<string> messageTypes, CancellationToken cancellationToken)`：`AsNoTracking`，按 `TurnId` 与 `messageTypes.Contains(MessageType)` 过滤，按 `TurnSequence` 升序，经 `ToEntry` 返回。

- [ ] **步骤 1：写失败的测试**
  - `TurnAcceptanceTests.AcceptAsync_Durable_CommitsRecordWithStartEvent` 增加断言：唯一事件记录的 `MessageType` 等于 `AgwMessageTypes.TurnStart`。
  - `TurnAcceptanceTests.EventTypes.cs`：
    - `AppendAsync_MixedMessages_StoresMessageTypes`：`AcceptAsync(task, durable: _kit.Coordinator)` 之后，在 `_kit.Services` 的 scope 中取得 `IAgentsDbContext` 与 `IDurableExecutionEventSequence`，调用 `DurableExecutionEvents.AppendAsync(..., turnId, leaseEpoch: 0, segmentIndex: 0, [running 状态消息, 没有 type 的文本消息, completed 状态消息], lookupCommitted: false, ct)`；状态消息用 `AgentflowMessageMapper.CreateNodeStatusMessage("input", ..., null, Guid.CreateVersion7().ToString("D"))`。`_kit.ReadEventsAsync(turnId)` 的 `MessageType` 依次为 `agw-turn-start`、`agentflow-node-status`、`null`、`agentflow-node-status`。
    - `ReadByTypesAsync_MixedEvents_ReturnsRequestedTypesInSequence`：同样的事件，`ReadByTypesAsync(db, turnId, [AgwMessageTypes.TurnStart, AgwMessageTypes.AgentflowNodeStatus], ct)` 的序号为 `[1, 2, 4]`。
  - `AgentflowCheckpointStoreTests.DistributedResume_StartEvent_CarriesSourceRevision` 增加断言：该事件记录的 `MessageType` 等于 `AgwMessageTypes.TurnStart`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Agents.Tests --filter-class "*TurnAcceptanceTests"`
预期：编译失败，`MessageType` 与 `ReadByTypesAsync` 不存在。

- [ ] **步骤 3：实现**

实体与配置按设计文档“数据库设计”一节，索引用 `builder.HasIndex(item => new { item.TurnId, item.MessageType, item.TurnSequence })`。`TurnAcceptanceWriter.CreateStartEvent` 在受理事务中写入 `AgwMessageTypes.TurnStart`。`AppendAsync`、`AddResumeStartEvent`、`DurableTurnUpgrade.Append` 使用 `AgwMessageClassifier.GetMessageType(message)` 读取类型。`UpgradeEventsAsync` 在反序列化事件并改写开始消息后写入 `entry.MessageType`。

- [ ] **步骤 4：运行测试，确认通过**

运行：`env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Agents.Tests --filter-class "*TurnAcceptanceTests" --filter-class "*AgentflowCheckpointStoreTests"`
预期：全部通过（测试数据库由模型直接创建，不依赖 migration）。

- [ ] **步骤 5：运行架构测试**

运行：`env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test tests/Agw.Architecture.Tests`
预期：通过；原始持久化访问清单有变化时，按测试输出精确更新清单。

- [x] **步骤 6：生成 migration**

按 `docs/1.Development.md` 的两条 `dotnet ef migrations add` 命令生成 SQLite 与 PostgreSQL 的 `AddExecutionStreamEntryMessageType` migration。

- [x] **步骤 7：检查生成的 migration**

两个 migration 都只有一个 `AddColumn`（`message_type`，可空 text）与一个 `CreateIndex`（三列），没有外键与其他表的变化；`AgwDbContextModelSnapshot` 只有对应的变化。之后运行 `dotnet build Agw.slnx`，预期成功。

- [ ] **步骤 8：格式化改动的 .cs 文件（不格式化生成的 migration 以外的无关文件）**

---

### 任务 3：节点状态快照类型与读取器

**文件：**
- 新增：`src/server/Agw.Agents.Execution/Agentflows/Messaging/AgentflowNodeStatusSnapshot.cs`
- 测试：新增 `tests/Agw.Agents.Tests/AgentflowNodeStatusSnapshotReaderTests.cs`；修改 `tests/Agw.Agents.Tests/AgentflowNodeStatusTests.cs`

**接口：**
- 消费：任务 1 的 `TurnMessageFactory.AgentflowRevisionKey`。
- 产出（namespace `Agw.Agents.Execution.Agentflows.Messaging`）：
  - `public sealed record AgentflowNodeStatusSnapshot` 使用显式构造函数初始化 `TurnId`、`AgentflowId`、`AgentflowRevision` 和 `Statuses`。
  - `public sealed record AgentflowNodeStatusEntry(string NodeId, string Status, string? InteractionId)`
  - `internal static class AgentflowNodeStatusSnapshotReader`，`public static AgentflowNodeStatusSnapshot? Read(IEnumerable<AgwMessage> messages)`。规则见设计文档 7.1。属性值用 `ToString()` 按文本读取，revision 用 `int.TryParse` 和 `CultureInfo.InvariantCulture` 校验。首条消息类型无效或 Agentflow Turn 缺少大于 0 的 revision 时抛出 `AgwException(ErrorCodes.AgentExecutionFailed, ...)`。

- [ ] **步骤 1：写失败的测试**
  - `AgentflowNodeStatusTests.cs`（`AgentflowTurnExecutorTests` 的 partial）新增 `Read_LinearAgentflowOutput_ReturnsEveryStatusInSequence`：与 `RunAsync_LinearAgentflow_WritesRunningAndCompletedForEveryNode` 相同的 fixture；在收集到的消息前加上 `TurnMessageFactory.CreateStarted(new TurnEnvelope(turnId, Guid.CreateVersion7(), fixture.Flow.Id, AgentRuntimeType.Agentflow, turnId.ToString("D"), 1))`；快照的 `TurnId`、`AgentflowId`、`AgentflowRevision` 正确，`Statuses` 的 `(NodeId, Status)` 与消息中 `agentflow-node-status` 的顺序完全相同。
  - `AgentflowNodeStatusSnapshotReaderTests`：
    - `Read_MessagesFromEventLog_ReadsJsonElementProperties`：开始消息与两条状态消息（其中一条 waiting 带 `interactionId`）各经 `JsonUtil.Serialize` 与 `JsonUtil.Deserialize<AgwMessage>` 往返后读取，结果与直接读取相同，`InteractionId` 保留。
    - `Read_AgentTurn_ReturnsNull`。
    - `Read_FirstMessageIsNotTurnStart_Throws`：错误码为 `ErrorCodes.AgentExecutionFailed.Code`。
    - `Read_AgentflowTurnWithoutRevision_Throws`：删除开始消息中的 `agentflowRevision` 后读取。

- [ ] **步骤 2：运行测试，确认失败**

运行：`env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentflowNodeStatusSnapshotReaderTests"`
预期：编译失败，类型不存在。

- [ ] **步骤 3：实现上面的类型与读取器**

- [ ] **步骤 4：运行测试，确认通过**

分两次运行：

```bash
env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Agents.Tests --filter-class "*AgentflowNodeStatusSnapshotReaderTests"
env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Agents.Tests --filter-method "*Read_LinearAgentflowOutput_ReturnsEveryStatusInSequence"
```
预期：全部通过。

- [ ] **步骤 5：格式化改动的 .cs 文件**

---

### 任务 4：InProcess 读取节点状态快照

**文件：**
- 修改：`src/server/Agw.Agents.Execution/Turns/TurnBroadcast.cs`
- 修改：`src/server/Agw.Agents.Execution/Inbound/Connections/ExecutionConnectionContext.cs`
- 修改：`src/server/Agw.Agents.Execution/Inbound/Connections/ExecutionConnection.cs`
- 修改：`src/server/Agw.Agents.Execution/Inbound/SignalR/ExecutionConnectionRegistry.cs`
- 测试：`tests/Agw.Agents.Tests/TurnBroadcastTests.cs`、`tests/Agw.Agents.Tests/ExecutionConnectionTests.cs`

**接口：**
- 消费：任务 3 的 `AgentflowNodeStatusSnapshotReader.Read`。
- 产出：
  - `internal IReadOnlyList<TurnBroadcastEntry> TurnBroadcast.ReadAll()`：在 `_gate` 锁内复制 `_entries`。
  - `internal AgentflowNodeStatusSnapshot? ExecutionConnectionContext.ReadAgentflowNodeStatuses()`：`_turnBroadcast` 为 null 时返回 null，否则读取 `ReadAll()` 中的消息。
  - `internal Task<AgentflowNodeStatusSnapshot?> ExecutionConnection.ReadAgentflowNodeStatusesAsync(CancellationToken cancellationToken)`：在 `_commandGate` 内读取；连接已释放时返回 null。
  - `public Task<AgentflowNodeStatusSnapshot?> ExecutionConnectionRegistry.GetInProcessAgentflowNodeStatusesAsync(Guid projectId, Guid conversationId, string userId, string callerConnectionId, CancellationToken cancellationToken)`：用 `FindInProcessExecutionAsync` 找到连接，找不到时返回 null。

- [ ] **步骤 1：写失败的测试**
  - `TurnBroadcastTests.ReadAll_StatusAfterPendingDelta_ReturnsDeltaThenStatus`：先写入一条可合并的流式文本增量，再写入一条节点状态消息；`ReadAll()` 返回两条，序号 1 是文本，序号 2 是状态。
  - `ExecutionConnectionTests`（沿用 `RecoverInProcessExecution_ForeignOrMissingConnection_DoesNotExposeOrInterruptTurn` 的 fixture 与 `GetConnections(registry)`）：
    - `GetInProcessAgentflowNodeStatusesAsync_RunningAgentTurn_ReturnsNull`：运行中的 Agent Turn（`holdTurnOpen: true`）返回 null。
    - `GetInProcessAgentflowNodeStatusesAsync_ForeignUser_ReturnsNull`：以其他用户查询返回 null。
    - `GetInProcessAgentflowNodeStatusesAsync_NoActiveTurn_ReturnsNull`：启动 Turn 之前查询返回 null。

- [ ] **步骤 2：运行测试，确认失败**

运行：`env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Agents.Tests --filter-class "*TurnBroadcastTests" --filter-class "*ExecutionConnectionTests"`
预期：编译失败，新方法不存在。

- [ ] **步骤 3：实现上面的方法**

- [ ] **步骤 4：运行测试，确认通过**（同步骤 2 的命令，预期全部通过）

- [ ] **步骤 5：格式化改动的 .cs 文件**

---

### 任务 5：Distributed 读取节点状态快照

**文件：**
- 修改：`src/server/Agw.Agents.Execution/Persistence/Durable/DurableExecutionStore.cs`
- 修改：`src/server/Agw.Agents.Execution/Runtimes/Durable/DurableExecutionCoordinator.cs`
- 测试：新增 `tests/Agw.Agents.Tests/TurnAcceptanceTests.NodeStatuses.cs`

**接口：**
- 消费：任务 2 的 `DurableExecutionEvents.ReadByTypesAsync`，任务 3 的读取器。
- 产出：
  - `internal Task<bool> DurableExecutionStore.IsTurnActiveAsync(Guid projectId, Guid conversationId, Guid turnId, CancellationToken cancellationToken)` 使用 `UserInfoUtil.RequiredUserId` 取得当前用户，按项目、对话、当前用户、活动状态和 `turnId` 检查目标 Turn。
  - `internal Task<AgentflowNodeStatusSnapshot?> DurableExecutionCoordinator.ReadAgentflowNodeStatusesAsync(Guid projectId, Guid conversationId, Guid turnId, CancellationToken cancellationToken)` 在任一标识为空时抛出 `AgwException(ErrorCodes.InvalidParam)`。在同一个 scope 内调用 `IsTurnActiveAsync`，找到目标后用 `ReadByTypesAsync` 读取它的 `agw-turn-start` 与 `agentflow-node-status` 并交给读取器。找不到目标时返回 `null`。

- [ ] **步骤 1：写失败的测试**（`TurnAcceptanceTests` 的 partial，使用 `AcceptAgentflowAsync(task, agentflowId, durable: _kit.Coordinator)` 与任务 2 的追加方式）
  - `ReadAgentflowNodeStatusesAsync_TwoActiveAgentflowTurns_ReturnsRequestedTurnStatuses` 在同一对话受理两个 Agentflow Turn，给其中一个追加 `input` running、一条文本、`input` completed。按各自 `turnId` 查询，结果分别包含这两条状态和空状态列表。
  - PostgreSQL 验证同一对话可以同时保留 Running 和 Queued Turn，并可查询指定 Turn 的快照。
  - `ReadAgentflowNodeStatusesAsync_OtherUser_ReturnsNull`。
  - `ReadAgentflowNodeStatusesAsync_AgentTurn_ReturnsNull`：用 `AcceptAsync(task, durable: _kit.Coordinator)` 受理的 Agent Turn。
  - `ReadAgentflowNodeStatusesAsync_FinishedExecution_ReturnsNull`：通过 `_kit.CreateContext()` 把执行记录的 `Status` 改为 `DurableExecutionStatus.Completed`。

- [ ] **步骤 2：运行测试，确认失败**

运行：`env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Agents.Tests --filter-method "*ReadAgentflowNodeStatusesAsync*"`
预期：编译失败，方法不存在。

- [ ] **步骤 3：实现上面的方法**

- [ ] **步骤 4：运行测试，确认通过**（同步骤 2 的命令）

- [ ] **步骤 5：运行架构测试**

运行：`env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test tests/Agw.Architecture.Tests`
预期：通过；清单有变化时按测试输出精确更新。

- [ ] **步骤 6：格式化改动的 .cs 文件**

---

### 任务 6：Hub 方法 `GetAgentflowNodeStatuses` 与文档

**文件：**
- 修改：`src/server/Agw.Agents.Execution/Inbound/SignalR/ExecutionHub.cs`
- 修改：`docs/ws-flow.md`、`src/server/Agw.Agents.Execution/README.md`、`src/server/Agw.Agents.Execution/Persistence/README.md`

**接口：**
- 消费：任务 4 的 `GetInProcessAgentflowNodeStatusesAsync`，任务 5 的 `ReadAgentflowNodeStatusesAsync`。
- 产出为 `public Task<AgentflowNodeStatusSnapshot?> GetAgentflowNodeStatuses(Guid projectId, Guid conversationId, Guid? turnId)`，经 `InvokeResultAsync` 调用。Distributed 传入待恢复的 Turn ID，由 `DurableExecutionCoordinator` 读取。InProcess 传入 `null`，由 registry 读取，`callerConnectionId` 取 `Context.ConnectionId`。

- [ ] **步骤 1：实现 Hub 方法**（带中英文 summary，说明返回 null 的情况）

- [ ] **步骤 2：更新文档**
  - `docs/ws-flow.md`：Turn 开始与结束消息的 `agentflowRevision`；Hub 方法 `GetAgentflowNodeStatuses` 的参数、返回结构与调用时机（设计文档“接口文档”与 7.2 节）。
  - `src/server/Agw.Agents.Execution/README.md`：第 39 行附近节点状态消息的说明后补充：页面重新载入后通过 `GetAgentflowNodeStatuses` 恢复。
  - `src/server/Agw.Agents.Execution/Persistence/README.md`：`execution_stream_entry` 的 `message_type` 列与索引用途。

- [ ] **步骤 3：构建并运行相关测试**

运行：`dotnet build Agw.slnx` 与 `env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test --project tests/Agw.Agents.Tests`
预期：构建成功，测试全部通过（与开始前记录的失败对比，没有新增失败）。

- [ ] **步骤 4：格式化改动的 .cs 文件**

---

### 任务 7：客户端协议（execution-types、execution-core）

**文件：**
- 修改：`src/clients/packages/execution-types/src/chat-events.ts`
- 修改：`src/clients/packages/execution-core/src/protocol.ts`、`src/clients/packages/execution-core/src/execution-status.ts`
- 测试：`src/clients/packages/execution-core/src/protocol.test.ts`、`src/clients/packages/execution-core/src/execution-status.test.ts`

**接口：**
- 产出：
  - `ExecutionHubMethod` 增加 `"GetAgentflowNodeStatuses"`。
  - `export type TurnTarget` 与 `export function getTurnTarget(message: ExecutionMessage): TurnTarget | null`（设计文档第 4 节）；不是 `agw-turn-start` 时返回 null；缺少 `turnId`、`agentId`、`agentType`，`agentType` 不是 `"agent"`/`"agentflow"`，或 Agentflow Turn 的 `agentflowRevision` 不是大于 0 的整数时抛出 `TypeError`。
  - `AgentflowNodeStatusEvent`、`AgentflowNodeSnapshotEvent`、扩展后的 `ExecutionStatusEvent`、`parseAgentflowNodeSnapshot(value: unknown): AgentflowNodeSnapshotEvent`（设计文档 7.2 节）；`interactionId` 为 null 时省略该字段。

- [ ] **步骤 1：写失败的测试**
  - `protocol.test.ts`：`getTurnTarget reads an Agentflow turn start`（得到 `{ turnId: "turn-1", agentId: "flow-1", agentType: "agentflow", agentflowRevision: 3 }`）；`getTurnTarget reads an Agent turn start`（`agentflowRevision: null`）；`getTurnTarget returns null for a finish message`；`getTurnTarget throws when an Agentflow turn start lacks agentflowRevision`；`getTurnTarget throws when a turn start lacks agentId`。
  - `execution-status.test.ts`：`parseAgentflowNodeSnapshot accepts a snapshot`（含 `interactionId: null` 与字符串两种状态）；`parseAgentflowNodeSnapshot rejects malformed snapshots`（缺少 `turnId`、`agentflowRevision` 为 0、`status` 为 `"paused"`、`statuses` 不是数组，各自抛出 `TypeError`）。

- [ ] **步骤 2：运行测试，确认失败**

运行（在 `src` 下）：`pnpm --filter @agw/execution-core exec tsx --test src/protocol.test.ts src/execution-status.test.ts`
预期：失败，函数不存在。

- [ ] **步骤 3：实现**

- [ ] **步骤 4：运行测试，确认通过**（同步骤 2 的命令）

- [ ] **步骤 5：类型检查**

运行（在 `src` 下）：`pnpm typecheck`
预期：只有开始前记录的失败。对 `ExecutionStatusEvent` 做穷尽检查的 `switch`（如果有）补上新类型的分支，处理为忽略。

---

### 任务 8：chat-core 节点运行状态与节点种类名称

**文件：**
- 新增：`src/clients/packages/chat-core/src/agentflow-node-run-state.ts`、`agentflow-node-run-state.test.ts`
- 新增：`src/clients/packages/chat-core/src/agentflow-node-labels.ts`
- 修改：`src/clients/packages/chat-core/src/index.ts`（`export *` 两个新文件）

**接口：**
- 消费：任务 7 的 `getTurnTarget`、`ExecutionStatusEvent`；`@agw/execution-core` 已有的 `getTurnIdentity`、`getTurnFinishedStatus`、`isSupersededTurnMessage`。
- 产出：
  - 设计文档第 5 节的 `AgentflowNodeRunState`、`EMPTY_AGENTFLOW_NODE_RUN_STATE`、`applyAgentflowNodeStatus`、`applyAgentflowNodeMessage`、`finishAgentflowNodeRun`、`getAgentflowNodeStatus`。
  - `export type AgentflowNodeKind = components["schemas"]["AgentflowNodeDto"]["kind"]`；`export const AGENTFLOW_NODE_LABELS: Record<AgentflowNodeKind, string>`，内容与 `agents/.../agentflow-node-labels.ts` 相同。

- [ ] **步骤 1：写失败的测试**（`agentflow-node-run-state.test.ts`）
  - `an Agentflow turn start resets the state to that turn`：从带有其他 Turn 状态的 state 开始，结果为 `{ turnId: "turn-1", agentflowId: "flow-1", agentflowRevision: 3, turnActive: true }` 且三个表为空。
  - `an Agent turn start keeps only the turn id`：`agentflowId` 为 null，`turnActive` 为 false。
  - `running then completed ends as completed`。
  - `a completed node that runs again shows running`。
  - `waiting lasts until its own interaction is answered`：waiting i1、waiting i2、running 带 i1 → `waiting`；completed 带 i2 → `completed`。
  - `a snapshot of a new turn replaces the state`。
  - `a snapshot followed by the replayed suffix equals folding every event`：事件 E1…E5（含 waiting 与回答），`fold(E1…E5)` 与 `fold(fold(snapshot(E1…E4)), E3…E5)` 的 `statusByNodeId`、`pendingInteractionIdsByNodeId` 相同。
  - `a finish of another turn leaves the state unchanged`：返回同一个对象。
  - `a finish removes running and pending and keeps completed and failed`：`status` 为 `interrupted` 的结束消息。
  - `a superseded turn start leaves the state unchanged`。
  - `member output marks the active member`：`nodePath` 为 `block/block.writer.manager` 时成员 id 为 `writer`。
  - `unchanged inputs return the same object`：没有 `nodeId` 的流式增量、同一个成员的第二条输出、`permission-status` 事件、对未激活状态调用 `finishAgentflowNodeRun`。

- [ ] **步骤 2：运行测试，确认失败**

运行（在 `src` 下）：`pnpm --filter @agw/chat-core exec tsx --test src/agentflow-node-run-state.test.ts`
预期：失败，模块不存在。

- [ ] **步骤 3：实现两个新文件并导出**

- [ ] **步骤 4：运行测试，确认通过**（同步骤 2 的命令）

---

### 任务 9：chat-core 节点顺序

**文件：**
- 新增：`src/clients/packages/chat-core/src/agentflow-node-order.ts`、`agentflow-node-order.test.ts`
- 修改：`src/clients/packages/chat-core/src/index.ts`

**接口：**
- 产出：`export function orderAgentflowNodes(definition: components["schemas"]["AgentflowDefinitionDto"]): components["schemas"]["AgentflowNodeDto"][]`，算法见设计文档第 2 节。`position.x`、`position.y` 的类型是 `number | string`，排序前用 `Number()` 转换。

- [ ] **步骤 1：写失败的测试**
  - `orders the design example by layer`：设计文档第 2 节的示例（Reviewer `y=0`、Tester `y=200`，`Gate → Planner` 为回边）得到 `["input", "planner", "gate", "coder", "reviewer", "tester", "output"]`。
  - `places a fan-in node after all of its predecessors`：`input→a→b→d`、`input→c→d` 时 `d` 排在 `b` 之后。
  - `sorts one layer by y, then x, then definition order`。
  - `starts from every input node`：两个 input。
  - `places nodes without edges by position`。
  - `compares string positions as numbers`：`"80"` 排在 `"120"` 之前。

- [ ] **步骤 2：运行测试，确认失败**

运行（在 `src` 下）：`pnpm --filter @agw/chat-core exec tsx --test src/agentflow-node-order.test.ts`
预期：失败，模块不存在。

- [ ] **步骤 3：实现并导出**

- [ ] **步骤 4：运行测试，确认通过**；再运行 `pnpm --filter @agw/chat-core test` 与 `pnpm --filter @agw/chat-core typecheck`，预期通过。

---

### 任务 10：编辑器改用 chat-core 的节点运行状态

**文件：**
- 修改：`src/clients/packages/agents/package.json`（`dependencies` 增加 `"@agw/chat-core": "workspace:*"`）、`src/pnpm-lock.yaml`（由 `pnpm install` 更新）
- 修改：`src/clients/packages/agents/src/ui-web/pages/agentflows/components/agentflow-run-state.ts`、`agentflow-run-state.test.ts`
- 删除：`src/clients/packages/agents/src/ui-web/pages/agentflows/components/agentflow-node-labels.ts`
- 修改：同目录的 `agentflow-document.ts`、`agentflow-node-meta.ts`（从 `@agw/chat-core` 引入 `AGENTFLOW_NODE_LABELS`）

**接口：**
- 消费：任务 8 的全部产出。
- 产出：`AgentflowRunState = AgentflowNodeRunState & { latestOutputByNodeId; nodeIdByMessageId }`；`EMPTY_RUN_STATE`、`applyAgentflowRunStatus`、`applyAgentflowRunMessage`、`getNodeRunStatus` 的名称与签名不变（设计文档第 8 节）。

- [ ] **步骤 1：增加依赖并安装**

在 `src` 下运行 `pnpm install`，预期 lockfile 只增加 `@agw/agents` 对 `@agw/chat-core` 的 workspace 依赖。

- [ ] **步骤 2：补全测试数据**

`agentflow-run-state.test.ts` 中的 `agw-turn-start` 消息补上 `agentId: "flow-1"`、`agentType: "agentflow"`、`agentflowRevision: 1`（与服务端消息一致）。

- [ ] **步骤 3：改写 `agentflow-run-state.ts` 并替换名称的引入位置，删除 `agentflow-node-labels.ts`**

- [ ] **步骤 4：运行测试与检查**

运行（在 `src` 下）：`pnpm --filter @agw/agents test`、`pnpm --filter @agw/agents typecheck`、`pnpm test:boundaries`
预期：全部通过。

---

### 任务 11：ExecutionSessionManager 保存节点运行状态

**文件：**
- 修改：`src/clients/packages/chat-runtime/src/execution-session-manager.ts`
- 测试：`src/clients/packages/chat-runtime/src/execution-session-manager.test.ts`；以及运行后失败、需要补全 Turn 开始消息字段的 chat-runtime 测试文件

**接口：**
- 消费：任务 8 的 `applyAgentflowNodeStatus`、`applyAgentflowNodeMessage`、`finishAgentflowNodeRun`、`EMPTY_AGENTFLOW_NODE_RUN_STATE`。
- 产出（与 `subscribeQueue` 相同，写成箭头函数属性，引用保持稳定）：
  - `public subscribeAgentflowNodeRuns = (listener: () => void): (() => void) => ...`
  - `public getAgentflowNodeRunVersion = (): number => ...`
  - `public getAgentflowNodeRun(key: ExecutionSessionKey): AgentflowNodeRunState`
  - `Entry` 增加 `agentflowNodeRun: AgentflowNodeRunState`；更新位置见设计文档第 6 节（`handleStatus`、`handleMessage` 开头、`handleReconnected` 无运行中 Turn 的分支、`handleClose`、`resumeCheckpoint` 失败分支与 `handleDispatchFailure` 中调用 `activity.turnFinished(..., "failed")` 处、`discard`）。
  - `handleStatus` 写入 `ActiveTurnSnapshot.statuses` 前移除同一 Turn 已保存的快照，每个 Turn 只保留最新快照。

- [ ] **步骤 1：写失败的测试**（沿用 `createExecutionClient` 与 `clientHandlers`）
  - `node run state keeps updating while no Chat is attached`：Agentflow 的 `agw-turn-start`（带 `agentId`、`agentType`、`agentflowRevision`），`detach()` 之后 `onStatus` 一条 running；`getAgentflowNodeRun(sessionKey).statusByNodeId` 为 `{ a: "running" }`。
  - `streaming deltas do not bump the node run version`：250 条没有 `nodeId` 的文本增量前后版本号相同。
  - `a reconnect without a running turn finishes the node run`：`hasRunningTurn` 为 false 时调用 `onReconnected`，`turnActive` 为 false，running 被移除。
  - `a connection close finishes the node run`。
  - `a snapshot status sets the turn when no turn start was seen`。
  - `discard removes the node run state`：返回 `EMPTY_AGENTFLOW_NODE_RUN_STATE`。

- [ ] **步骤 2：运行测试，确认失败**

运行（在 `src` 下）：`pnpm --filter @agw/chat-runtime exec tsx --test src/execution-session-manager.test.ts`
预期：新用例失败。

- [ ] **步骤 3：实现**

- [ ] **步骤 4：运行整个包的测试并补全测试数据**

运行：`pnpm --filter @agw/chat-runtime test`。因 `getTurnTarget` 抛出 `TypeError` 而失败的用例，把其中的 `agw-turn-start` 测试消息补上 `agentId` 与 `agentType`（与服务端 `CreateTurnProperties` 一致），不修改被测代码。预期全部通过。

---

### 任务 12：ExecutionSession 恢复时读取节点状态快照

**文件：**
- 修改：`src/clients/packages/chat-runtime/src/execution-session.ts`
- 修改：`src/clients/packages/test-harness/src/execution-hub.ts`（`defaultResult` 增加 `case "GetAgentflowNodeStatuses": return null;`）
- 测试：`src/clients/packages/chat-runtime/src/execution-hub.test.ts`

**接口：**
- 消费：任务 7 的 `parseAgentflowNodeSnapshot`。
- 产出：`private async restoreAgentflowNodeStatuses(expectedTurnId: string | null, expectedInProcessConnectionId?: string): Promise<void>` 通过 `GetAgentflowNodeStatuses` 读取并校验快照。有预期 `turnId` 时，只交出相同 Turn 的快照。InProcess 页面重新载入时，收到快照后通过 `FindInProcessExecution` 确认原连接仍负责该对话，再交给 `onStatus`。恢复版本变化时结束处理。
- 调用：`restoreDurableSubscription` 在 cursor 有值时，先用 turnId 恢复快照，再发送 `SubscribeTurnCommand`。`recoverInProcessExecution` 在旧连接仍活动且本次轮询没有请求中断时，传入当前 turnId 和原连接 ID，然后调用 `scheduleRecoveryPoll()`。

- [ ] **步骤 1：写失败的测试**
  - `durable restore with a cursor delivers the node snapshot before resubscribing`：localStorage 中保存 `{ turnId: "turn-1", cursor: "9" }`；Hub 对 `GetAgentflowNodeStatuses` 返回 turn-1 的快照；`onStatus` 收到 `type` 为 `agentflow-node-snapshot` 的事件；调用记录中 `GetAgentflowNodeStatuses`（参数 `["project-1", "conversation-1", "turn-1"]`）排在 `SubscribeTurnCommand` 之前。
  - `durable restore ignores a snapshot of another turn`。
  - `durable restore without a cursor does not read node statuses`：保存的 `cursor` 为 null。
  - `in-process recovery delivers a node snapshot on every active poll`：与 `Retry recovers a WebSocket-only Agentflow turn ...` 相同的定时器方式，`RecoverInProcessExecution` 两次返回 true，`onStatus` 收到两次快照。
  - `a malformed node snapshot fails the restore`：返回 `{ turnId: "turn-1" }`，`configure` 以 `TypeError` 失败。

- [ ] **步骤 2：运行测试，确认失败**

运行（在 `src` 下）：`pnpm --filter @agw/chat-runtime exec tsx --test src/execution-hub.test.ts`
预期：新用例失败。

- [ ] **步骤 3：实现，并修改 test-harness 的默认结果**

- [ ] **步骤 4：运行测试并修正现有的 Hub 测试连接**

运行同一命令。`execution-hub.test.ts` 中自己实现 `invoke` 的测试连接对未知方法返回 `undefined`，在 Distributed 带 cursor 恢复或 InProcess 恢复的用例中会被 `parseAgentflowNodeSnapshot` 拒绝；在这些连接中对 `GetAgentflowNodeStatuses` 返回 `null`。预期全部通过；再运行 `pnpm --filter @agw/chat-runtime test`，预期通过。

---

### 任务 13：节点卡片组件 `ChatAgentflowNodes`

**文件：**
- 新增：`src/clients/packages/chat/src/ui-web/components/message/chat-agentflow-nodes.tsx`、`chat-agentflow-nodes.test.ts`

**接口：**
- 消费：任务 8 的 `AgentflowNodeRunState`、`getAgentflowNodeStatus`、`AGENTFLOW_NODE_LABELS`；任务 9 的 `orderAgentflowNodes`。
- 产出：`export function ChatAgentflowNodes(props: { agentflowId: string; revision: number; run: AgentflowNodeRunState }): React.JSX.Element`。定义用 `apiGet("/api/agentflows/detail", { params: { query: { id: agentflowId, revision } } })` 读取；结构、样式、标题取值与滚动规则见设计文档第 3 节。
- 高亮行使用 `ring-1 ring-inset` 绘制内描边，节点行和 Block 成员列表各由独立组件渲染。

- [ ] **步骤 1：写失败的测试**（参照 `agent-selector.test.ts`：`setupDomEnvironment`、`startApiServer`、`QueryClientProvider`；`GET /api/agentflows/detail` 用函数路由按 `record.query` 返回定义，`GET /api/agents`、`GET /api/agentflows` 返回名称数据）
  - `nodes appear in execution order with their kind labels`：列表项的标题顺序与 `orderAgentflowNodes` 一致；标题与种类名称相同时没有副标题。
  - `running and waiting nodes are highlighted`：两行带 `aria-current="step"`，图标的 `aria-label` 为 `Running`、`Waiting`。
  - `completed and failed nodes show their icons`。
  - `block members are listed and the active member is marked while the block runs`：成员行 `aria-current="true"`；Block 不是 running 时没有该属性。
  - `titles fall back to the referenced agent, agentflow, checkpoint name, then kind label`。
  - `a long node name stays on one line and keeps the full name in title`：标题元素带 `truncate` 类，`title` 属性为完整名称（用 60 个字符的中文名称）。
  - `a failed definition request shows the error`：detail 路由返回 404，卡片显示 `getApiErrorMessage` 的文字。
  - `node ids unknown to the definition are ignored`。
  - `the first highlighted node scrolls into view when highlights change`：用 `t.mock.method(window.HTMLElement.prototype, "scrollIntoView")` 记录调用；运行状态从无高亮变为节点 b running 时，调用目标是 b 的行，参数为 `{ block: "nearest" }`；高亮集合不变的重新渲染不再调用。

- [ ] **步骤 2：运行测试，确认失败**

运行（在 `src` 下）：`pnpm --filter @agw/chat exec tsx --test src/ui-web/components/message/chat-agentflow-nodes.test.ts`
预期：失败，模块不存在。

- [ ] **步骤 3：实现组件**

- [ ] **步骤 4：运行测试，确认通过**（同步骤 2 的命令）

---

### 任务 14：侧栏与 Chat 接入

**文件：**
- 新增：`src/clients/packages/chat/src/ui-web/components/message/aside-agentflow.ts`、`aside-agentflow.test.ts`
- 修改：`src/clients/packages/chat/src/ui-web/components/message/chat-aside.tsx`、`chat-aside.test.ts`（传入 `agentflow: null`）
- 修改：`src/clients/packages/chat/src/ui-web/components/message/chat.tsx`
- 新增：`src/clients/packages/chat/src/ui-web/components/message/chat-agentflow-aside.test.ts`
- 修改：运行后失败、需要补全 Turn 开始消息字段的 chat 测试文件（例如 `chat-token-usage.test.ts`、`chat-draft-conversation.test.ts`、`pages/chat/chat-workspace-hydration.test.ts`）
- 修改：`src/clients/web/e2e/chat-api.ts`、`src/clients/web/e2e/chat-layout.spec.ts`

**接口：**
- 消费：任务 11 的 manager 方法，任务 13 的 `ChatAgentflowNodes`。
- 产出：
  - `export function selectAsideAgentflow(nodeRun: AgentflowNodeRunState, target: { id: string; type: ChatTargetType; agentflowRevision?: number } | null, agentflows: readonly { id: string; publishedRevision: number }[]): { agentflowId: string; revision: number; run: AgentflowNodeRunState } | null`，实现设计文档第 1 节的表；revision 不大于 0 时返回 null。
  - `ChatAsideProps.agentflow`（设计文档第 1 节）；卡片放在 Token usage 卡片与 Todo 卡片之间。
  - `Chat` 用 `React.useSyncExternalStore(executionSessionManager.subscribeAgentflowNodeRuns, executionSessionManager.getAgentflowNodeRunVersion, executionSessionManager.getAgentflowNodeRunVersion)` 订阅，`queueKey` 为 null 时使用 `EMPTY_AGENTFLOW_NODE_RUN_STATE`；`["agentflows"]` 查询只在 target 为 Agentflow 或节点运行状态有 `agentflowId` 时启用。

- [ ] **步骤 1：写失败的测试**
  - `aside-agentflow.test.ts`：设计文档第 1 节表中的四种情况各一个用例，以及 `an unpublished agentflow is not shown`。
  - `chat-agentflow-aside.test.ts`（参照 `chat-token-usage.test.ts` 的 `replaceExecutionHub` 与 API 服务）：
    - `an Agentflow target shows its nodes and an Agent target does not`。
    - `node statuses move the highlight`：推送 Agentflow 的 `agw-turn-start`（revision 1）与 `input` completed、`writer` running，`writer` 行带 `aria-current="step"`。
    - `an interrupted turn clears highlights and keeps completed nodes`：推送 `status: "interrupted"` 的结束消息后没有 `aria-current="step"` 的行，`input` 行仍有 `Completed` 图标。
    - `a running Agentflow stays visible after the selector changes to an Agent`。
  - `chat-layout.spec.ts`：`an Agentflow conversation lists its nodes between Token usage and Todo`。`mockChatApi` 增加选项：`/api/agentflows` 返回一个 `publishedRevision: 1` 的 Agentflow，`/api/agentflows/detail` 返回 40 个线性节点的定义，对话详情的 `resumeState` 为 `{ targetType: "agentflow", targetId: <id>, agentMode: null }`；`/api/hubs/` 下的请求用 `route.abort()` 结束。桌面视口下：`Agentflow nodes` 卡片可见，它的上边位于 Token usage 卡片下边之下；节点列表容器高度不超过 448px，`scrollHeight` 大于 `clientHeight`；DOM 中有 40 个节点行，第一行是 `Input`。手机视口下卡片不可见。

- [ ] **步骤 2：运行测试，确认失败**

运行（在 `src` 下）：`pnpm --filter @agw/chat exec tsx --test src/ui-web/components/message/aside-agentflow.test.ts src/ui-web/components/message/chat-agentflow-aside.test.ts`
预期：失败。

- [ ] **步骤 3：实现**

- [ ] **步骤 4：运行 chat 包的全部测试并补全测试数据**

运行：`pnpm --filter @agw/chat test`。因 `getTurnTarget` 抛出 `TypeError` 而失败的用例，把测试中的 `agw-turn-start` 消息补上 `agentId` 与 `agentType`。预期全部通过。

- [ ] **步骤 5：运行 Web e2e**

在仓库根目录运行 `dotnet build src/server/Agw.Standalone.Host`；在 `src/clients` 下运行 `pnpm --filter @agw/web exec playwright install chromium` 与 `pnpm --filter @agw/web test:e2e -- chat-layout.spec.ts`。
预期：`chat-layout.spec.ts` 的全部用例在两个视口下通过。

---

### 任务 15：验收

**文件：**
- 验收脚本位于 `.scratch/agentflow-node-status-e2e/`（`node-status-e2e.ts`、`run-inprocess.sh`、`run-distributed.sh`）。

- [x] **步骤 1：编写验收脚本**
  - 使用本地 Ollama 的 `qwen3:0.6b` 模型，通过 `OllamaNative` provider 发出请求，测试模型的输出上限为 512 token。
  - `node-status-e2e.ts`（用 `createRequire(<repo>/src/clients/packages/test-harness/package.json)` 取得 `@microsoft/signalr`）：登录（antiforgery → login → tokens 取 Bearer）；创建模型 provider、两个 Agent、Agentflow（`nodeId: "input"` 的 Input 节点 → `writer` → `reviewer` → Output）并发布；项目用 `default-built-in`。连接 A 发送 `SettingCommand` 与 `ExecCommand`，收集全部消息。在 `writer` 的等待期间，连接 B（同一用户、新连接，模拟重新载入）调用 `GetAgentflowNodeStatuses(projectId, conversationId, turnId)`，Distributed 传入 A 的 Turn ID，InProcess 传入 `null`。
  - 检查：A 收到的 `agw-turn-start` 带 `agentflowRevision: 1`；B 得到的快照 `turnId` 与 A 的 Turn 相同，`statuses` 与 A 在调用时刻之前收到的节点状态消息逐条相同，并包含 `writer` 的 `running`；Turn 结束后 B 再次调用得到 `null`；以另一个用户调用得到 `null`。
  - `run-inprocess.sh`：用独立的 SQLite 数据库启动 InProcess Standalone Host（端口 30918），完成检查后关闭 Host。
  - `run-distributed.sh`：使用 `.scratch/postgres-tests.json` 中的 PostgreSQL 服务器，新建独立数据库，以 `--Execution:Provider=Distributed`、PostgreSQL 数据库与 `--DistributedLock:Provider=postgres` 启动 Standalone Host，运行同一脚本；脚本在 Distributed 模式下额外让连接 B 在取得快照后以 A 的 turnId 和快照时刻的 cursor 发送 `SubscribeTurnCommand`，检查之后收到的状态与快照合并后等于 A 收到的全部状态。

- [x] **步骤 2：运行 `run-inprocess.sh`**

revision 为 1，恢复 7 条状态，完整 Turn 收到 14 条状态。其他用户请求和 Turn 结束后的请求均返回 null。

- [x] **步骤 3：运行 `run-distributed.sh`**

revision 为 1，恢复 7 条状态，完整 Turn 收到 14 条状态。其他用户请求和 Turn 结束后的请求均返回 null。快照与重放的状态合并后，与原连接收到的状态逐条一致。

- [ ] **步骤 4：完整检查**

运行：仓库根目录 `dotnet build Agw.slnx`、`env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test Agw.slnx`、`dotnet csharpier check .`；在 `src` 下 `pnpm test`、`pnpm lint`、`pnpm fmt:check`。
预期：与开始前记录的失败对比，没有新增失败。

- [ ] **步骤 5：向用户报告**

列出每个任务的测试结果、两份验收脚本的输出，以及生成的 migration 文件。
