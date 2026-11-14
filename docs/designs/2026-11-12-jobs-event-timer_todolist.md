# Jobs 统一到 Events 的 Timer 调度：实施计划

> **给执行计划的 agent：** 必须使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 按任务逐个实施。步骤使用 checkbox（`- [ ]`）记录进度。

**目标：** Job 的定时、触发、执行、重试和运行记录全部由 Events 提供，`Agw.Jobs` 只保留 API、Agent 工具和请求校验；项目会话记录自己的类型和对应的事件。

**架构：** 每个 Job 是一个 Timer Event Source 加一条 Agent 或 Agentflow 订阅，两条记录的 `managed_by` 都是 `job`，`managed_key` 保存相同的 Job ID。`Agw.Events` 通过 `Agw.Events.Contracts` 中的 `IEventJobFacade` 向 `Agw.Jobs` 提供读取和修改。后台执行为事件投递创建的会话写入 `kind = event:<来源类型>` 和 `relate_id = 事件 ID`。

**技术栈：** .NET 10、ASP.NET Core Minimal API、EF Core（SQLite、PostgreSQL）、xUnit v3、Next.js、TypeScript、Node test runner（`tsx --test`）。

**设计文档：** `docs/designs/2026-11-12-jobs-event-timer.md`。执行每个任务之前阅读设计文档的对应章节。

## 全局约束

- 没有兼容性要求：现有的 Job、运行记录、Jobs 接口格式和执行行为都不保留。
- Job 的字段使用 Events 中对应字段的名称和格式：`name`、`enabled`、`projectId`、`targetKind`、`agentId`、`instructions`、`schedule.kind`、`schedule.at`、`schedule.interval`、`schedule.cron`、`dueAt`、`scheduleCompleted`、`lastDelivery`。
- Timer 固定取值：时区 `UTC`，事件类型 `job.timer.fired`，`subject` 是 Job ID（`D` 格式）。
- Job 订阅固定取值：`source_ids_json` 只包含 Job 的 Timer，`event_types_json` 为 `["job.timer.fired"]`，`filter_json` 为空，`permission_mode` 为 `FullAccess`。
- 会话类型取值：`native`，或者 `event:<来源类型>`，来源类型是 `EventSourceKind` 的小写名称（`timer`、`webhook`、`httppoll`、`provider`、`publisher`）。`native` 会话的 `relate_id` 为空，`event:` 会话的 `relate_id` 是触发事件的 `event.id`。
- 接口：JSON 接口返回 Bens.Results（`ApiResult.Ok(...)`），标识放在 query 或 body 中，新增路由不使用路径参数。预期失败抛出 `AgwException(ErrorCodes.X)`。
- 当前用户通过 `UserInfoUtil.RequiredUserId` 或 `IUserInfoService` 读取，不增加 `userId` 参数。
- 测试：xUnit v3，命名为 `Method_Condition_ExpectedResult`，使用真实实现和真实的 SQLite、PostgreSQL，不使用 mock。测试运行时产生的文件保存在测试项目的输出目录或 `.scratch/` 下。
- 注释使用中文和英文两种语言，数量保持克制。时间使用 `DateTimeOffset` 和 `TimeProvider`。
- 格式化：C# 使用 `dotnet csharpier format <修改过的文件>`；客户端在 `src/clients` 下使用 `pnpm fmt`。
- 修改项目引用、实体、持久化访问或 Behavior 之后运行 `dotnet test --project tests/Agw.Architecture.Tests`；修改 `ErrorCodes` 之后运行 `dotnet test --project tests/Agw.Shared.Tests`。
- 过滤参数在本机无效时，先执行 `dotnet build tests/<项目>`，再运行 `dotnet tests/<项目>/bin/Debug/net10.0/<项目>.dll -class "<完整类名>"`。
- 任务 2、任务 6、任务 7 各生成一个 migration（SQLite 和 PostgreSQL 各一份），命令见 `docs/1.Development.md`。执行生成命令之前向用户确认。migration 中没有外键操作。
- 每个任务最后的提交步骤只在用户要求提交时执行，提交信息使用 Conventional Commits。不使用 Git 回退代码。
- 手动启动 Host 时使用 `.scratch/` 下独立的数据目录、显式的 SQLite 连接和空闲端口，不使用默认数据路径。
- 创建子代理时，提示词要求它使用 Rider MCP 工具查找和阅读代码。

## 复核重点

下面五种输入或状态在设计文档中没有单独的用例，每一项的测试已经加入对应任务。

1. `schedule.interval` 带有日期部分（`P1D`）：按照 Timer 的规则接受，第一次触发是当前时间加上完整的间隔。测试在任务 3。
2. Job 创建之后执行目标被删除：到期后投递记录错误并等待重试，Job 的查询照常返回，`lastDelivery.lastError` 有内容。测试在任务 3。
3. 删除 Job 时已有尚未派发的 `job.timer.fired` 事件：事件的派发结果为 `NoMatch`，不产生投递。测试在任务 3。
4. 请求中的 `targetKind` 为 `2`，或者缺少 `schedule`：返回 `InvalidParam`，不保存任何记录。测试在任务 5。
5. 运行历史的 `offset` 为负数，`limit` 为 `0` 或 `501`：返回 `InvalidParam`。测试在任务 3 和任务 5。

## 文件结构

| 位置 | 内容 |
| --- | --- |
| `src/server/Agw.Shared/Contracts/Conversations/ConversationKinds.cs` | 会话类型的取值 |
| `src/server/Agw.Events.Contracts/EventJobs.cs` | `IEventJobFacade` 和它的 DTO |
| `src/server/Agw.Events/Application/Jobs/EventJobFacade.cs` | facade 的实现 |
| `src/server/Agw.Events/Application/Sources/EventTimerScheduleSync.cs` | 按照 Timer 配置建立或取消计划，`EventSourceAppService` 和 `EventJobFacade` 共用 |
| `src/server/Agw.Jobs/Api`、`Application`、`Contracts`、`Domain`、`Infrastructure` | 路由、应用服务、工具、DTO、默认名称和目标校验 |
| `src/server/Agw.Infrastructure/Projects/ProjectDeletionCoordinator.cs`、`Agents/AgentDeletionCoordinator.cs` | Job 记录的删除和引用检查 |
| `src/server/Agw.Projects`、`Agw.Projects.Contracts`、`Agw.Data/Entities/Projects` | 会话的 `Kind` 和 `RelateId` |
| `src/server/Agw.Agents.Execution/Inbound/Facades/BackgroundExecutionFacade.cs` | 为事件投递写入会话类型 |
| `src/server/Agw.Migrations.Sqlite`、`Agw.Migrations.Postgres` | 各自的 `MoveJobsToEventTimers` migration |
| `tests/ModuleCompositionTestHost.cs` | 多个测试项目共用的完整模块组合 |
| `src/clients/packages/jobs`、`events`、`projects`、`projects-core`、`api` | 客户端页面和类型 |

任务 6 删除的内容：`src/server/Agw.Jobs/Scheduling`、`Execution`、`Domain/Behaviors`、`Domain/ValueObjects`、`Application/Persistence`，`src/server/Agw.Infrastructure/Jobs`，`src/server/Agw.Infrastructure/Repositories/JobRepo.cs`，`src/server/Agw.Data/Entities/Jobs`。

---

### 任务 1：会话类型取值和新增错误码

**文件：**
- 创建：`src/server/Agw.Shared/Contracts/Conversations/ConversationKinds.cs`
- 修改：`src/server/Agw.Shared/Exceptions/ErrorCodes.cs`
- 测试：`tests/Agw.Shared.Tests/ConversationKindsTests.cs`

**接口：**
- 产出：
  - `ConversationKinds.Native`（`"native"`）、`ConversationKinds.EventPrefix`（`"event:"`）
  - `static string ConversationKinds.ForEventSource(EventSourceKind kind)`
  - `static bool ConversationKinds.IsEvent(string kind)`：只有等于某个 `ForEventSource` 的结果时返回 `true`
  - `ErrorCodes.EventSourceManaged = new(409_0034, "Event source is managed by a Job.", HttpStatusCode.Conflict)`

- [x] **步骤 1：编写失败的测试**

```csharp
[Theory]
[InlineData(EventSourceKind.Timer, "event:timer")]
[InlineData(EventSourceKind.Webhook, "event:webhook")]
[InlineData(EventSourceKind.HttpPoll, "event:httppoll")]
[InlineData(EventSourceKind.Provider, "event:provider")]
[InlineData(EventSourceKind.Publisher, "event:publisher")]
public void ForEventSource_EachKind_ReturnsLowercaseEventKind(EventSourceKind kind, string expected)
{
    Assert.Equal(expected, ConversationKinds.ForEventSource(kind));
    Assert.True(ConversationKinds.IsEvent(expected));
}

[Theory]
[InlineData("native")]
[InlineData("event:")]
[InlineData("event:Timer")]
[InlineData("event:unknown")]
[InlineData("timer")]
public void IsEvent_OtherValues_ReturnsFalse(string kind) => Assert.False(ConversationKinds.IsEvent(kind));
```

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Shared.Tests --filter-class "*ConversationKindsTests"`
预期：编译失败，`ConversationKinds` 不存在。

- [x] **步骤 3：实现 `ConversationKinds` 并增加 `EventSourceManaged`**

`ConversationKinds` 与 `ConversationCreators` 放在同一个目录，写法相同（`public static class`，带有中英文注释）。

- [x] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Shared.Tests`
预期：全部通过。

- [ ] **步骤 5：提交**

`git commit -m "feat(shared): add conversation kinds and EventSourceManaged"`

---

### 任务 2：Events 的表结构和配套规则

**文件：**
- 创建：`tests/ModuleCompositionTestHost.cs`
- 修改：`tests/Agw.Events.Tests/Agw.Events.Tests.csproj`（`<Compile Include="../ModuleCompositionTestHost.cs" Link="ModuleCompositionTestHost.cs" />`）
- 修改：`src/server/Agw.Data/Entities/Events/EventSource.cs`、`EventSourceConfiguration.cs`、`EventSubscription.cs`、`EventSubscriptionConfiguration.cs`、`EventDeliveryConfiguration.cs`、`EventDeliveryAttempt.cs`、`EventDeliveryAttemptConfiguration.cs`
- 修改：`src/server/Agw.Events/Application/Sources/EventSourceAppService.cs`（`EnsureEditable`）
- 修改：`src/server/Agw.Events/Application/EventManagementAppService.cs`（`CheckEditable`、`Subscription`）、`EventManagementModels.cs`
- 修改：`src/server/Agw.Events/Api/Dtos/EventSourceDtos.cs`、`src/server/Agw.Events/Api/EventSourcesEndpointRouteBuilderExtensions.cs`（`ToResponse`）
- 修改：`src/server/Agw.Events/Infrastructure/Delivery/EventDeliveryProcessor.cs`（`ClaimAsync`）
- 修改：`src/server/Agw.Events/Application/Delivery/EventDeliveryManagementAppService.cs`（`CancelAsync`）
- 数据库变更纳入任务 7 的 `MoveJobsToEventTimers` migration。
- 创建测试：`tests/Agw.Events.Tests/EventJobManagedRecordTests.cs`
- 修改测试：`tests/Agw.Events.Tests/EventDeliveryIntegrationTests.cs`

**接口：**
- 使用：任务 1 的 `ErrorCodes.EventSourceManaged`
- 产出：
  - `Agw.Shared/Contracts/Events/EventManagedBy`：`User = "user"`、`Job = "job"`、`ExtensionPrefix = "ext:"`、`ForExtension(Guid extensionId)`、`IsExtension(string managedBy)`
  - `EventSource.ManagedBy`、`EventSubscription.ManagedBy`（`string`，默认 `EventManagedBy.User`）和 `EventSource.ManagedKey`（`string?`）；`EventSubscription` 移除 `ManagedByExtensionId`；两张表各自一个 `(create_by, managed_by, managed_key)` 唯一索引，筛选条件 `managed_key IS NOT NULL`
  - `ManagedEventSubscriptionDefinition.HandlerExtensionId`（`Guid`），`EventSubscriptionBehavior.UpsertManaged` 用它写入 `HandlerExtensionId`
  - `EventDeliveryAttempt.TurnId`（`Guid?`）
  - 索引 `event_delivery(subscription_id, id)`、`event_delivery_attempt(delivery_id, attempt)`
  - `EventSourceResponse.ManagedBy`、`EventSourceResponse.ManagedKey` 和 `EventSubscriptionResponse.ManagedBy`；`EventSubscriptionResponse` 移除 `ManagedByExtensionId`
  - 测试使用的 `Agw.Tests.ModuleCompositionTestHost`：

```csharp
internal sealed class ModuleCompositionTestHost : IAsyncDisposable
{
    public IServiceProvider Services { get; }

    // settings 覆盖默认配置。settings override the default configuration.
    public static Task<ModuleCompositionTestHost> CreateAsync(
        bool outboxEnabled = false,
        string? rootDirectory = null,
        string transportProvider = "InProcess",
        string? redisConnectionString = null,
        TimeSpan? staleAfter = null,
        IReadOnlyDictionary<string, string?>? settings = null
    );

    // 向 services 登记同一组模块，返回使用的配置。Registers the same modules into services and returns the configuration.
    public static IConfiguration ConfigureServices(
        IServiceCollection services,
        string root,
        IReadOnlyDictionary<string, string?>? settings = null
    );

    // 初始化 extension 包目录并创建数据库。Initializes the extension package catalog and creates the database.
    public static Task InitializeAsync(IServiceProvider services);

    public static Task<TransportDelivery> ReadTransportDeliveryAsync(IEventTransport transport, string topic);
}
```

- [x] **步骤 1：移动测试 host**

把 `EventDeliveryIntegrationTests` 中的 `DeliveryTestHost` 和 `ReadTransportDeliveryAsync` 移到 `tests/ModuleCompositionTestHost.cs`，`CreateAsync` 保留原有参数并增加 `settings`，登记模块的部分放在 `ConfigureServices`，初始化的部分放在 `InitializeAsync`。`EventDeliveryIntegrationTests` 改为使用它。
运行：`dotnet test --project tests/Agw.Events.Tests`，预期全部通过。

- [x] **步骤 2：编写失败的测试**

`EventJobManagedRecordTests`（使用 `ModuleCompositionTestHost.CreateAsync()`，用户为 `owner`）：

```csharp
// 种子：Kind = Timer、ManagedBy = job 的 EventSource，分别调用 EventSourceAppService 的 UpdateAsync、SetEnabledAsync、DeleteAsync
[Theory]
[InlineData("update")]
[InlineData("enable")]
[InlineData("delete")]
public async Task SourceMutation_JobManagedSource_ThrowsEventSourceManaged(string operation)
Assert.Equal(ErrorCodes.EventSourceManaged.Code, exception.Code);
Assert.Equal("Event source is managed by a Job.", exception.Message);
Assert.Equal(versionBefore, source.Version);
Assert.Null(source.DeletedAt);

// 种子：ManagedBy 为 job 或 ext:<extensionId> 的 EventSubscription，分别调用 SaveSubscriptionAsync（修改）和 DeleteSubscriptionAsync
[Theory]
[InlineData("update", "job", "Event subscription is managed by a Job.")]
[InlineData("delete", "job", "Event subscription is managed by a Job.")]
[InlineData("update", "ext:019a4c17-0000-7000-8000-000000000001", "Event subscription is managed by an extension.")]
[InlineData("delete", "ext:019a4c17-0000-7000-8000-000000000001", "Event subscription is managed by an extension.")]
public async Task SubscriptionMutation_ManagedSubscription_ThrowsEventSubscriptionManaged(string operation, string managedBy, string message)
Assert.Equal(ErrorCodes.EventSubscriptionManaged.Code, exception.Code);
Assert.Equal(message, exception.Message);
Assert.True(await db.EventSubscriptions.AnyAsync(row => row.Id == subscriptionId, ct));

[Fact]
public async Task GetSubscriptionAsync_JobManagedSubscription_ReturnsManagedByAndManagedKey()
Assert.Equal("job", response.ManagedBy);
Assert.Equal(subscription.ManagedKey, response.ManagedKey);
```

`EventDeliveryIntegrationTests` 中修改两个已有的用例：

```csharp
// ProcessAsync_AmbiguousAgentRetry_ReusesTurnIdAndOriginalRequestSnapshot 的末尾增加
var attempts = await db.EventDeliveryAttempts.AsNoTracking().Where(row => row.DeliveryId == deliveryId).ToListAsync(ct);
Assert.Equal(2, attempts.Count);
Assert.All(attempts, attempt => Assert.Equal(turnId, attempt.TurnId));

// CancelAsync_WaitingExecution_RegistersDeliveryOwnedCancellation 的种子增加一条尝试记录
// （DeliveryId = deliveryId、Attempt = 1、TurnId = turnId、StartedAt = EndedAt = 一分钟之前、Result = Retry），末尾增加
var attempt = await db.EventDeliveryAttempts.AsNoTracking().SingleAsync(row => row.DeliveryId == deliveryId, ct);
Assert.Equal(EventDeliveryAttemptResult.Failed, attempt.Result);
Assert.Equal("The event delivery was cancelled.", attempt.Error);
Assert.True(attempt.EndedAt > attempt.StartedAt);
Assert.Equal(turnId, attempt.TurnId);
```

- [x] **步骤 3：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Events.Tests`
预期：编译失败，`ManagedBy`、`EventSource.ManagedKey` 和 `EventDeliveryAttempt.TurnId` 不存在。

- [x] **步骤 4：实现**

- 实体和 EF 配置按照“接口”一节增加字段和索引，唯一索引的写法与 `EventSourceConfiguration` 中 `publisher_key` 的筛选索引相同。
- `EventManagedRecordGuard.EnsureUserManaged(string managedBy, ErrorCode error, string recordName)`（`Agw.Events/Application`）：`managedBy` 是 `user` 时返回；是 `job` 时抛出消息为 `"{recordName} is managed by a Job."` 的 `AgwException(error, ...)`；其他取值的消息为 `"{recordName} is managed by an extension."`。
- `EventSourceAppService.EnsureEditable` 调用 `EnsureUserManaged(source.ManagedBy, ErrorCodes.EventSourceManaged, "Event source")`。
- `EventManagementAppService.CheckEditable` 调用 `EnsureUserManaged(subscription.ManagedBy, ErrorCodes.EventSubscriptionManaged, "Event subscription")`。
- `EventSubscriptionFacade` 按照 `ManagedBy == EventManagedBy.ForExtension(extensionId)` 和 `ManagedKey` 查找订阅，创建时写入相同的 `ManagedBy`。
- `EventSubscriptionMatcher.IsTrustedPublisher`：`EventManagedBy.IsExtension(subscription.ManagedBy)` 时，可信的 publisher key 是 `agw.execution`、`agw.notifications`、`subscription.ManagedBy` 和 `agw.scheduler:{subscription.ManagedBy}`。
- 两个响应类型增加 `ManagedBy`，来源的响应增加 `ManagedKey`，并在映射中填写。
- `EventDeliveryProcessor.ClaimAsync`：新建 `EventDeliveryAttempt` 时写入 `TurnId = turnId`。
- `EventDeliveryManagementAppService.CancelAsync`：取消 `WaitingExecution` 的投递并且投递更新成功时，在同一个事务内把 `DeliveryId` 等于这条投递、`Attempt` 等于投递当前 `Attempt` 的尝试记录改为 `EndedAt = now`、`Result = Failed`、`Error = "The event delivery was cancelled."`。更新的行数不等于 1 时抛出 `new AgwException(ErrorCodes.InvalidParam, "The event delivery attempt record is missing.")`。

- [x] **步骤 5：检查 Events 的 migration 操作**

检查 `Up`：`event_source` 增加 `managed_by`、`managed_key`，`event_subscription` 增加 `managed_by` 并把 `managed_by_extension_id` 的值写成 `ext:<extensionId>` 之后删除这一列，两张表各有 `(create_by, managed_by, managed_key)` 唯一筛选索引；`event_delivery_attempt` 增加 `turn_id`；增加 `event_delivery(subscription_id, id)` 和 `event_delivery_attempt(delivery_id, attempt)` 两个索引。

- [x] **步骤 6：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Events.Tests`、`dotnet test --project tests/Agw.Architecture.Tests`、`dotnet test --project tests/Agw.Projects.Tests --filter-class "*InitialMigrationTests"`、`dotnet test --project tests/Agw.Settings.Tests --filter-class "*SettingsMigrationTests"`
预期：全部通过。

- [ ] **步骤 7：提交**

`git commit -m "feat(events): mark job-managed records and record attempt turn ids"`

---

### 任务 3：`IEventJobFacade`

**文件：**
- 创建：`src/server/Agw.Events.Contracts/EventJobs.cs`
- 创建：`src/server/Agw.Events/Application/Jobs/EventJobFacade.cs`
- 创建：`src/server/Agw.Events/Application/Sources/EventTimerScheduleSync.cs`
- 修改：`src/server/Agw.Events/Application/Sources/EventSourceAppService.cs`（`SyncTimerAsync` 和 `ParseTemplateData` 移到 `EventTimerScheduleSync`）
- 修改：`src/server/Agw.Events/DependencyInjection.cs`（登记 `EventTimerScheduleSync` 和 `IEventJobFacade`）
- 测试：`tests/Agw.Events.Tests/EventJobFacadeTests.cs`

**接口：**
- 使用：任务 2 的字段和 `ModuleCompositionTestHost`；已有的 `IEventScheduler`、`IEventIngestTransaction`、`IApplicationLock`、`EventSourceConfigParser.ParseTimer`
- 产出（`Agw.Events`）：

```csharp
public sealed class EventTimerScheduleSync
{
    public EventTimerScheduleSync(IEventScheduler scheduler);

    // 来源启用并且没有删除时按照 ConfigJson 建立计划，其他情况取消计划。
    // Upserts the schedule from ConfigJson when the source is enabled and not deleted; cancels it otherwise.
    public Task SyncAsync(EventSource source, long requestSeq, CancellationToken cancellationToken);
}
```

- 产出（`Agw.Events.Contracts`）：

```csharp
public interface IEventJobFacade
{
    Task<EventJob> CreateAsync(EventJobDefinition definition, CancellationToken cancellationToken);
    Task<EventJob> UpdateAsync(Guid jobId, EventJobDefinition definition, CancellationToken cancellationToken);
    Task<EventJob> SetEnabledAsync(Guid jobId, bool enabled, CancellationToken cancellationToken);
    // 返回删除之前的 Job。Returns the Job as it was before deletion.
    Task<EventJob> DeleteAsync(Guid jobId, CancellationToken cancellationToken);
    Task<EventJob?> FindAsync(Guid jobId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EventJob>> ListAsync(Guid? projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EventJobRun>> ListRunsAsync(Guid jobId, int offset, int limit, CancellationToken cancellationToken);
    Task<int> CountAsync(CancellationToken cancellationToken);
}

public enum EventJobTargetKind { Agent = 0, Agentflow = 1 }

public sealed record EventJobSchedule
{
    public required string Kind { get; init; }
    public DateTimeOffset? At { get; init; }
    public string? Interval { get; init; }
    public string? Cron { get; init; }
}

public sealed record EventJobDefinition
{
    public required string Name { get; init; }
    public required bool Enabled { get; init; }
    public required Guid ProjectId { get; init; }
    public required EventJobTargetKind TargetKind { get; init; }
    public required Guid AgentId { get; init; }
    public required string Instructions { get; init; }
    public required EventJobSchedule Schedule { get; init; }
}

public sealed record EventJob
{
    public required Guid Id { get; init; }
    public required EventJobDefinition Definition { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public bool ScheduleCompleted { get; init; }
    public EventJobDelivery? LastDelivery { get; init; }
}

// State 的数值与 EventDeliveryState 相同。State uses the values of EventDeliveryState.
public sealed record EventJobDelivery
{
    public required Guid Id { get; init; }
    public required int State { get; init; }
    public required int Attempt { get; init; }
    public string? LastError { get; init; }
}

// Result 的数值与 EventDeliveryAttemptResult 相同。Result uses the values of EventDeliveryAttemptResult.
public sealed record EventJobRun
{
    public required Guid Id { get; init; }
    public required Guid DeliveryId { get; init; }
    public required int Attempt { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required int Result { get; init; }
    public string? Error { get; init; }
    public Guid? TurnId { get; init; }
}
```

**facade 的规则（设计文档“管理定时任务”）：**
- `EventJobFacade` 是 `public sealed class`，构造函数参数为 `IEventsDbContext`、`EventTimerScheduleSync`、`IEventIngestTransaction`、`IEventJobProjectLock`、`IApplicationLock`、`TimeProvider`。Job 属于 `UserInfoUtil.RequiredUserId`。
- 修改、启停和删除先取得应用锁 `events:job:{jobId:N}`，再通过 `IEventIngestTransaction.ExecuteAsync` 开启事务并读取 Timer 和订阅。创建、修改、启停和删除在事务内锁定涉及的 Project 行。更新在读取 Job 之后遇到 Project 删除时返回 `JobModificationConflict`。
- 创建：使用 `Guid.CreateVersion7()` 生成 Job ID 和 Timer ID。Timer 的 `Kind = Timer`、`CeSource = /agw/sources/{sourceId:N}`，`ConfigJson` 包含 `kind`、`timeZone = "UTC"`、`event = { type: "job.timer.fired", subject: jobId }`，以及 `schedule.kind` 对应的一个字段（`Once` 写入 `at`，`Interval` 写入 `interval`，`Cron` 写入 `cron`），保存之前通过 `EventSourceConfigParser.ParseTimer` 校验。
- 名称为空白或者超过 200 个字符、指令为空白、`TargetKind` 不是已定义的枚举值时抛出 `InvalidParam`。
- 计划通过 `EventTimerScheduleSync.SyncAsync` 建立和取消。创建时 `requestSeq = 1`，之后是保存之前读取到的 `source.Version + 1`。
- 调用 `SyncAsync` 的时机：创建启用的 Job；修改时 `schedule`（`Kind` 和它对应的字段）变化或者启用状态变化；启停时启用状态变化；删除。其他修改不调用它，计划的 `due_at` 和 `revision` 保持不变。
- 删除：Timer 的 `Enabled = false`、`NextRunAt = null`、`DeletedAt = now`，调用 `SyncAsync`，删除订阅记录。
- `DbUpdateConcurrencyException` 转为 `AgwException(ErrorCodes.JobModificationConflict)`。
- `DueAt`：`CallerKey` 和 `BusinessKey` 都是 `source:{sourceId}` 并且 `State == Active` 的计划的 `DueAt`。
- `ScheduleCompleted` 表示同一计划的 `State` 为 `Completed`。
- `LastDelivery`：Job 订阅的投递按照 `Id` 倒序的第一条（查询使用 `OrderByDescending(row => row.Id)`）。
- `ListAsync`：返回当前用户的 Job，`projectId` 不为空时只返回这个 Project 的 Job；按照 `DueAt` 升序排列，`DueAt` 为空的排在最后，再按照名称排列。
- `ListRunsAsync`：`offset < 0`、`limit < 1` 或者 `limit > 500` 时抛出 `InvalidParam`。返回 Job 订阅全部投递的尝试，按照 `StartedAt` 倒序、再按照 `Id` 倒序分页；投递处于 `Running` 或 `WaitingExecution` 时，`Attempt` 等于投递当前 `Attempt` 的尝试不返回。
- `CountAsync`：当前用户 `ManagedBy` 为 `job` 并且 `DeletedAt` 为空的 Timer 数量。

- [x] **步骤 1：编写失败的测试**

`EventJobFacadeTests` 使用 `ModuleCompositionTestHost.CreateAsync()`，用户为 `owner`。触发的写法：`EventScheduleRunner.RunAsync(schedule.Id)` 发布事件，`ReadTransportDeliveryAsync(transport, EventTopics.Accepted)` 的结果交给 `EventDispatcher.DispatchAsync`，`ReadTransportDeliveryAsync(transport, EventTopics.Delivery)` 的结果交给 `EventDeliveryProcessor.ProcessAsync`。

```csharp
[Fact]
public async Task CreateAsync_CronJob_SavesTimerSubscriptionAndActiveSchedule()
Assert.Equal(EventSourceKind.Timer, source.Kind);
Assert.Equal(EventManagedBy.Job, source.ManagedBy);
Assert.Equal(job.Id.ToString("D"), source.ManagedKey);
Assert.Equal(EventManagedBy.Job, subscription.ManagedBy);
Assert.Equal(job.Id.ToString("D"), subscription.ManagedKey);
Assert.Equal($"[\"{source.Id}\"]", subscription.SourceIdsJson);
Assert.Equal("[\"job.timer.fired\"]", subscription.EventTypesJson);
Assert.Null(subscription.FilterJson);
Assert.Equal((int)AgentExecutionPermissionMode.FullAccess, subscription.PermissionMode);
Assert.Equal("UTC", config.GetProperty("timeZone").GetString());
Assert.Equal("job.timer.fired", config.GetProperty("event").GetProperty("type").GetString());
Assert.Equal(job.Id.ToString("D"), config.GetProperty("event").GetProperty("subject").GetString());
Assert.Equal($"source:{source.Id}", schedule.CallerKey);
Assert.Equal(schedule.CallerKey, schedule.BusinessKey);
Assert.Equal(EventScheduleState.Active, schedule.State);
Assert.Equal(schedule.DueAt, job.DueAt);
Assert.Null(job.LastDelivery);

[Fact]
public async Task CreateAsync_DisabledJob_SavesNoSchedule()
Assert.Empty(await db.EventSchedules.ToListAsync(ct));
Assert.Null(job.DueAt);

[Theory]
[InlineData("Interval", "PT0S", null)]
[InlineData("Cron", null, "not a cron")]
[InlineData("Cron", null, "0 0 1 * * *")]
[InlineData("Once", null, null)]
[InlineData("Weekly", null, null)]
public async Task CreateAsync_InvalidSchedule_ThrowsInvalidParamAndSavesNothing(string kind, string? interval, string? cron)
Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
Assert.Empty(await db.EventSources.ToListAsync(ct));
Assert.Empty(await db.EventSubscriptions.ToListAsync(ct));
Assert.Empty(await db.EventSchedules.ToListAsync(ct));

// 名称长度为 200 时创建成功
[Theory]
[InlineData(201, "run")]
[InlineData(0, "run")]
[InlineData(10, " ")]
public async Task CreateAsync_InvalidNameOrInstructions_ThrowsInvalidParam(int nameLength, string instructions)

// CREATE TRIGGER reject_job_subscription BEFORE INSERT ON event_subscription BEGIN SELECT RAISE(ABORT, 'rejected'); END;
[Fact]
public async Task CreateAsync_SubscriptionInsertRejected_SavesNothing()
await Assert.ThrowsAsync<DbUpdateException>(() => facade.CreateAsync(definition, ct));
// 在新的作用域中断言
Assert.Empty(await db.EventSources.ToListAsync(ct));
Assert.Empty(await db.EventSchedules.ToListAsync(ct));

[Fact]
public async Task CreateAsync_IntervalWithDays_SchedulesAfterOneDay()   // 复核重点 1，interval = "P1D"
Assert.InRange(job.DueAt!.Value - before, TimeSpan.FromDays(1), TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));

[Fact]
public async Task UpdateAsync_NameProjectTargetInstructions_KeepsScheduleDueAtAndRevision()
Assert.Equal(dueAtBefore, schedule.DueAt);
Assert.Equal(revisionBefore, schedule.Revision);
Assert.Equal("renamed", source.Name);
Assert.Equal("renamed", subscription.Name);
Assert.Equal(newProjectId, subscription.ProjectId);

[Fact]
public async Task UpdateAsync_ScheduleChanged_IncrementsScheduleRevision()
Assert.Equal(revisionBefore + 1, schedule.Revision);
Assert.Equal("0 2 * * *", updated.Definition.Schedule.Cron);

[Fact]
public async Task SetEnabledAsync_DisableThenEnable_CancelsAndRestoresSchedule()
// 停用之后
Assert.Equal(EventScheduleState.Cancelled, schedule.State);
Assert.False(source.Enabled);
Assert.False(subscription.Enabled);
Assert.Null(disabled.DueAt);
// 启用之后
Assert.Equal(EventScheduleState.Active, schedule.State);
Assert.NotNull(enabled.DueAt);

// at 早于当前时间
[Fact]
public async Task CreateAsync_OnceInPast_FiresOneEventAndOneDelivery()
Assert.True(await runner.RunAsync(schedule.Id, ct));
Assert.False(await runner.RunAsync(schedule.Id, ct));
Assert.Equal(EventScheduleState.Completed, schedule.State);
Assert.Null((await facade.FindAsync(job.Id, ct))!.DueAt);
var record = await db.Events.SingleAsync(row => row.Type == "job.timer.fired", ct);
Assert.Equal($"{schedule.Id}:{schedule.Revision}:1", record.CeId);
Assert.Equal(job.Id.ToString("D"), record.Subject);
// 派发之后
Assert.Single(await db.EventDeliveries.Where(row => row.SubscriptionId == subscription.Id).ToListAsync(ct));

[Fact]
public async Task UpdateAsync_CompletedOnceRenamed_DoesNotFireAgain()
Assert.False(await runner.RunAsync(schedule.Id, ct));
Assert.Equal(1, await db.Events.CountAsync(row => row.Type == "job.timer.fired", ct));

[Fact]
public async Task SetEnabledAsync_ReenableCompletedOnce_FiresAgain()
Assert.True(await runner.RunAsync(schedule.Id, ct));
Assert.Equal(2, await db.Events.CountAsync(row => row.Type == "job.timer.fired", ct));

// Interval Job：把计划的 DueAt 改为当前时间之前并运行，重复两次，第一条投递保持 Pending
[Fact]
public async Task RunAsync_PreviousDeliveryUnfinished_CreatesNewDelivery()
Assert.Equal(2, await db.EventDeliveries.CountAsync(row => row.SubscriptionId == subscription.Id, ct));

[Fact]
public async Task DeleteAsync_ExistingJob_MarksTimerDeletedRemovesSubscriptionAndCancelsSchedule()
Assert.NotNull(source.DeletedAt);
Assert.False(source.Enabled);
Assert.False(await db.EventSubscriptions.AnyAsync(row => row.ManagedBy == EventManagedBy.Job && row.ManagedKey == jobKey, ct));
Assert.Equal(EventScheduleState.Cancelled, schedule.State);
Assert.Null(await facade.FindAsync(job.Id, ct));

[Fact]
public async Task DeleteAsync_PendingTimerEvent_DispatchesAsNoMatch()   // 复核重点 3
Assert.Equal(EventDispatchState.NoMatch, record.DispatchState);
Assert.Empty(await db.EventDeliveries.ToListAsync(ct));

// 事件已经派发，投递还没有被领取，这时删除 Job，再处理投递
[Fact]
public async Task DeleteAsync_UnclaimedDelivery_IsCancelledWhenProcessed()
Assert.Equal(EventDeliveryState.Cancelled, delivery.State);

[Fact]
public async Task FindAsync_OtherUsersJob_ReturnsNull()

[Theory]
[InlineData("update")]
[InlineData("enable")]
[InlineData("delete")]
[InlineData("runs")]
public async Task Operation_OtherUsersJob_ThrowsJobNotFound(string operation)
Assert.Equal(ErrorCodes.JobNotFound.Code, exception.Code);

[Fact]
public async Task ListAsync_ProjectFilterAndDeliveries_ReturnsLatestDeliveryOrderedByDueAt()
Assert.All(filtered, job => Assert.Equal(projectId, job.Definition.ProjectId));
Assert.Equal(latestDeliveryId, jobs[0].LastDelivery!.Id);
Assert.Null(jobs[^1].DueAt);

// agentId 指向不存在的 Agent：运行计划、派发、处理投递之后
[Fact]
public async Task FindAsync_TargetMissing_ReturnsLastDeliveryError()   // 复核重点 2
Assert.Equal((int)EventDeliveryState.RetryScheduled, job.LastDelivery!.State);
Assert.Equal(1, job.LastDelivery.Attempt);
Assert.False(string.IsNullOrWhiteSpace(job.LastDelivery.LastError));

// 种子：两条已经结束的尝试和一条 WaitingExecution 投递的当前尝试
[Fact]
public async Task ListRunsAsync_FinishedAndRunningAttempts_ReturnsFinishedNewestFirst()
Assert.Equal([secondFinishedAttemptId, firstFinishedAttemptId], runs.Select(run => run.Id));
Assert.Equal([firstFinishedAttemptId], (await facade.ListRunsAsync(job.Id, 1, 1, ct)).Select(run => run.Id));

[Theory]
[InlineData(-1, 10)]
[InlineData(0, 0)]
[InlineData(0, 501)]
public async Task ListRunsAsync_OffsetOrLimitOutOfRange_ThrowsInvalidParam(int offset, int limit)   // 复核重点 5

[Fact]
public async Task CountAsync_DeletedAndOtherUsersJobs_AreNotCounted()
Assert.Equal(1, await facade.CountAsync(ct));
```

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Events.Tests --filter-class "*EventJobFacadeTests"`
预期：编译失败，`IEventJobFacade` 不存在。

- [x] **步骤 3：实现**

`EventSourceAppService` 的三处 `SyncTimerAsync` 调用改为 `EventTimerScheduleSync.SyncAsync`。在 `AddEvents` 中登记 `services.AddScoped<EventTimerScheduleSync>()` 和 `services.AddScoped<IEventJobFacade, EventJobFacade>()`。

- [x] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Events.Tests`、`dotnet test --project tests/Agw.Architecture.Tests`
预期：全部通过。

- [ ] **步骤 5：提交**

`git commit -m "feat(events): add job facade over timer sources and subscriptions"`

---

### 任务 4：删除 Project 和删除 Agent 时处理 Job 的记录

**文件：**
- 修改：`src/server/Agw.Infrastructure/Projects/ProjectDeletionCoordinator.cs`
- 修改：`src/server/Agw.Infrastructure/Agents/AgentDeletionCoordinator.cs`
- 测试：`tests/Agw.Projects.Tests/ProjectDeletionCoordinatorEventTests.cs`、`tests/Agw.Projects.Tests/AgentDeletionCoordinatorTests.cs`（两个文件都属于 partial class `ProjectDeletionCoordinatorTests`）

**接口：**
- 使用：任务 2 的 `ManagedBy`、`ManagedKey` 和 `EventManagedBy`
- 产出：`ProjectDeletionCoordinator` 的私有方法 `Task DeleteEventJobsAsync(ProjectDeletionTarget target, CancellationToken cancellationToken)`

- [x] **步骤 1：编写失败的测试**

```csharp
// ProjectDeletionCoordinatorEventTests.cs
// 种子：两个 Job（各自的 Timer、订阅、Active 计划），其中一个属于被删除的 Project
[Fact]
public async Task DeleteProjectAsync_ProjectHasJobs_DeletesJobRecordsOfThatProjectOnly()
Assert.NotNull(deletedSource.DeletedAt);
Assert.False(deletedSource.Enabled);
Assert.Equal(sourceVersionBefore + 1, deletedSource.Version);
Assert.Equal(EventScheduleState.Cancelled, deletedSchedule.State);
Assert.Null(deletedSchedule.DueAt);
Assert.False(await db.EventSubscriptions.AnyAsync(row => row.ManagedKey == deletedJobKey, ct));
Assert.Null(otherSource.DeletedAt);
Assert.Equal(EventScheduleState.Active, otherSchedule.State);
Assert.True(await db.EventSubscriptions.AnyAsync(row => row.ManagedKey == otherJobKey, ct));

// AgentDeletionCoordinatorTests.cs：DeleteAgentAsync_DefinitionReference_RejectsWithoutDeletingState 增加两个取值，
// 种子为 ManagedBy = job、TargetKind = Agent、AgentId = agent.Id 的 EventSubscription，Enabled 分别为 true 和 false
[InlineData("enabled-job-subscription")]
[InlineData("disabled-job-subscription")]

// 种子：ManagedBy = user、目标是这个 Agent 的 EventSubscription
[Fact]
public async Task DeleteAgentAsync_UserSubscriptionReference_DeletesAgent()
Assert.True(deleted);
Assert.False(await context.Agents.AnyAsync(item => item.Id == agent.Id, token));
```

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Projects.Tests --filter-class "*ProjectDeletionCoordinatorTests"`
预期：`DeleteProjectAsync_ProjectHasJobs_DeletesJobRecordsOfThatProjectOnly` 和两个新的取值失败。

- [x] **步骤 3：实现**

`DeleteEventJobsAsync` 在 `DeleteProjectAsync` 中“停用订阅”的语句之前调用，在已有的事务内依次执行：

1. 读取这个 Project 下 `CreateBy == target.OwnerUserId` 并且 `ManagedBy` 为 `job` 的订阅的 `ManagedKey`。
2. 读取 `ManagedBy` 为 `job` 并且 `ManagedKey` 属于这些值的 Timer ID。
3. `EventSchedules`：`CallerKey` 属于 `source:{sourceId}` 并且 `State == Active` 的计划，通过 `ExecuteUpdateAsync` 改为 `State = Cancelled`、`DueAt = null`，`Revision` 和 `Version` 各增加 1，写入 `UpdateBy`、`UpdateTime`。
4. `EventSources`：通过 `ExecuteUpdateAsync` 改为 `Enabled = false`、`NextRunAt = null`、`DeletedAt = now`，`Version` 增加 1，写入 `UpdateBy`、`UpdateTime`。
5. `EventSubscriptions`：通过 `ExecuteDeleteAsync` 删除这些 Job 的订阅记录。

`AgentDeletionCoordinator.DeleteAsync` 在已有的引用检查中增加一个条件：存在 `ManagedBy == EventManagedBy.Job`、`TargetKind == EventSubscriptionTargetKind.Agent`、`AgentId == agentId`、`CreateBy == ownerUserId` 的订阅时抛出 `AgentInUse`。

- [x] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Projects.Tests --filter-class "*ProjectDeletionCoordinatorTests"`、`dotnet test --project tests/Agw.Architecture.Tests`
预期：全部通过。

- [ ] **步骤 5：提交**

`git commit -m "feat(projects): delete job-managed event records with their project"`

---

### 任务 5：Jobs 模块改为调用 facade

**文件：**
- 修改：`src/server/Agw.Jobs/Agw.Jobs.csproj`（增加 `Agw.Events.Contracts` 引用）
- 重写：`src/server/Agw.Jobs/Api/EndpointRouteBuilderExtensions.cs`、`Application/Services/JobAppService.cs`、`Application/Tools/JobManagementToolExecutor.cs`、`Application/Facades/JobMetricsFacade.cs`、`Infrastructure/JobRepository.cs`、`Contracts/Tools/JobToolResponse.cs`
- 修改：`src/server/Agw.Jobs/Application/Skills/JobManagementSkill.cs`、`Domain/Repositories/IJobRepository.cs`、`Domain/Services/JobDefinitionDomainService.cs`、`DependencyInjection.cs`
- 创建：`src/server/Agw.Jobs/Contracts/JobRequests.cs`、`Contracts/JobResponses.cs`
- 删除：`src/server/Agw.Jobs/Contracts/JobCreateRequest.cs`、`JobUpdateRequest.cs`、`JobEnabledUpdateRequest.cs`、`JobLogResponse.cs`
- 修改 `AddJobs` 的调用：`src/server/Agw.Host/Program.cs:293`、`tests/ModuleCompositionTestHost.cs`、`tests/Agw.Agents.Tests/ExecutionWorkspaceAuthorizationTests.cs`、`ExecutionWorkspaceProvisioningTests.cs`、`ProviderSessionExecutionHarness.cs`、`tests/Agw.Files.Tests/ProjectWorkspaceRecreationTests.cs`、`tests/Agw.Host.Tests/HostModuleCompositionTests.cs`、`ProjectWorkspaceIsolationTests.cs`
- 修改：`tests/Agw.Architecture.Tests/BackendArchitectureTests.cs`（`Agw.Jobs` 的允许引用增加 `Agw.Events.Contracts`）
- 修改：`tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj`（引用 `Agw.Host`、`Agw.Events`，链接 `../ModuleCompositionTestHost.cs`）、`tests/Agw.Host.Tests/Agw.Host.Tests.csproj`（链接 `../ModuleCompositionTestHost.cs`）
- 重写测试：`tests/Agw.Jobs.Tests/JobsApiTests.cs`、`JobAppServiceTests.cs`、`JobManagementSkillTests.cs`、`EndpointExtensionTests.cs`
- 创建测试：`tests/Agw.Jobs.Tests/JobsPostgresTests.cs`
- 删除测试：`tests/Agw.Jobs.Tests` 中的 `JobHostedServiceTests.cs`、`JobAttemptRunnerIntegrationTests.cs`、`JobAttemptOutcomeRecorderTests.cs`、`InProcessJobRecoveryTests.cs`、`DurableJobRecoveryHostedServiceTests.cs`、`JobStoreTests.cs`、`JobBehaviorTests.cs`、`JobAgentExecutorTests.cs`、`JobSchedulerWakeSignalTests.cs`、`JobLogContractTests.cs`、`JobLockCancellationPostgresTests.cs`、`ProjectExecutionLockPostgresTests.cs`、`ProjectExecutionLockRouterTests.cs`、`InMemoryProjectExecutionLockTests.cs`、`JobTestServices.cs`、`TestRuntimeFacades.cs`、`TestUserInfoService.cs`、`SqliteJobTestDatabase.cs`、`DatabaseCommandGate.cs`
- 修改测试：`tests/Agw.Host.Tests/HostModuleCompositionTests.cs`、`DashboardControllerTests.cs`
- 修改：`.github/scripts/verify-postgres-tests.py`、`.github/workflows/build-server.yml`

**接口：**
- 使用：任务 3 的 `IEventJobFacade`；已有的 `AgentDefinitionLock`、`IProjectTaskFacade.ResolveConversationIdsAsync`、`IAgentCatalogFacade.IsOwnedTargetAsync`
- 产出：`AddJobs(this IServiceCollection services)`，不登记任何 `IHostedService`
- 产出（路由，分组为 `endpoints.MapGroup("api/jobs").WithTags("Jobs")`）：

| 方法与路径 | 处理 |
| --- | --- |
| `GET list?projectId=` | `JobAppService.ListAsync` |
| `GET get?id=` | `JobAppService.GetAsync` |
| `POST create` | `JobAppService.CreateAsync` |
| `POST update` | `JobAppService.UpdateAsync` |
| `POST enable` | `JobAppService.SetEnabledAsync` |
| `POST delete` | `JobAppService.DeleteAsync` |
| `GET runs/list?jobId=&offset=&limit=` | `JobAppService.ListRunsAsync`，`offset` 默认 0，`limit` 默认 100 |

- 产出（`Agw.Jobs.Contracts`，请求的字段都可以缺少，由 `JobAppService` 校验）：

```csharp
public sealed record JobScheduleDto
{
    public string? Kind { get; init; }
    public DateTimeOffset? At { get; init; }
    public string? Interval { get; init; }
    public string? Cron { get; init; }
}

public sealed record JobCreateRequest
{
    public string? Name { get; init; }
    public bool Enabled { get; init; } = true;
    public Guid ProjectId { get; init; }
    public AgentRuntimeType TargetKind { get; init; }
    public Guid AgentId { get; init; }
    public string? Instructions { get; init; }
    public JobScheduleDto? Schedule { get; init; }
}

// JobUpdateRequest 的字段与 JobCreateRequest 相同，另有 Guid Id。
public sealed record JobEnableRequest { public Guid Id { get; init; } public bool Enabled { get; init; } }
public sealed record JobIdRequest { public Guid Id { get; init; } }

public sealed record JobResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required bool Enabled { get; init; }
    public required Guid ProjectId { get; init; }
    public required AgentRuntimeType TargetKind { get; init; }
    public required Guid AgentId { get; init; }
    public required string Instructions { get; init; }
    public required JobScheduleResponse Schedule { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public required bool ScheduleCompleted { get; init; }
    public JobDeliveryResponse? LastDelivery { get; init; }
}

public sealed record JobScheduleResponse
{
    public required string Kind { get; init; }
    public DateTimeOffset? At { get; init; }
    public string? Interval { get; init; }
    public string? Cron { get; init; }
}

public sealed record JobDeliveryResponse
{
    public required Guid Id { get; init; }
    public required int State { get; init; }
    public required int Attempt { get; init; }
    public string? LastError { get; init; }
}

public sealed record JobRunResponse
{
    public required Guid Id { get; init; }
    public required Guid DeliveryId { get; init; }
    public required int Attempt { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required int Result { get; init; }
    public string? Error { get; init; }
    public Guid? ConversationId { get; init; }
}
```

- 产出（`JobAppService`，构造函数参数为 `IEventJobFacade`、`JobDefinitionDomainService`、`IProjectTaskFacade`、`IApplicationLock`、`TimeProvider`、`IUserInfoService`）：

```csharp
Task<IReadOnlyList<JobResponse>> ListAsync(Guid? projectId, CancellationToken cancellationToken);
Task<JobResponse> GetAsync(Guid id, CancellationToken cancellationToken);
Task<JobResponse> CreateAsync(JobCreateRequest request, CancellationToken cancellationToken);
Task<JobResponse> UpdateAsync(JobUpdateRequest request, CancellationToken cancellationToken);
Task<JobResponse> SetEnabledAsync(JobEnableRequest request, CancellationToken cancellationToken);
Task<JobResponse> DeleteAsync(Guid id, CancellationToken cancellationToken);
Task<IReadOnlyList<JobRunResponse>> ListRunsAsync(Guid jobId, int offset, int limit, CancellationToken cancellationToken);
```

- 产出（Domain）：
  - `IJobRepository.CountAsync(CancellationToken cancellationToken)`，取代 `CountByOwnerAsync`；`JobRepository` 通过 `IEventJobFacade.CountAsync` 实现
  - `JobDefinitionDomainService.EnsureTargetsVisibleAsync(Guid projectId, AgentRuntimeType targetKind, Guid agentId, string ownerUserId)`
  - `JobDefinitionDomainService.ResolveNameAsync(string? requestedName, DateTimeOffset now, CancellationToken cancellationToken)`
- 产出（工具参数）：
  - `agw_job_create(string instructions, AgentRuntimeType targetKind, Guid agentId, string kind, string? name = null, DateTimeOffset? at = null, string? interval = null, string? cron = null, bool enabled = true)`
  - `agw_job_update(Guid jobId, string? name = null, string? instructions = null, AgentRuntimeType? targetKind = null, Guid? agentId = null, string? kind = null, DateTimeOffset? at = null, string? interval = null, string? cron = null, bool? enabled = null)`
  - `agw_job_list()`、`agw_job_get(Guid jobId)`、`agw_job_delete(Guid jobId, string confirmation)` 的参数保持不变
  - 工具返回 `JobToolResponse`，字段与 `JobResponse` 相同

**规则：**
- `JobAppService.GetAsync` 在 `FindAsync` 返回 `null` 时抛出 `AgwException(ErrorCodes.JobNotFound)`。
- 创建和修改在 `AgentDefinitionLock.GetResourceName(当前用户)` 的锁内执行：修改先读取 Job（不存在时抛出 `JobNotFound`）；`TargetKind` 不是已定义的枚举值、缺少 `Schedule` 或者 `Schedule.Kind`、指令为空白时抛出 `InvalidParam`；然后调用 `EnsureTargetsVisibleAsync` 和 `ResolveNameAsync`，最后调用 facade。
- `JobMetricsFacade.GetAsync` 返回 `IEventJobFacade.CountAsync` 的结果。
- `ListRunsAsync`：使用 `EventJobRun.TurnId` 调用 `IProjectTaskFacade.ResolveConversationIdsAsync` 得到 `ConversationId`。
- 工具只处理当前 Project 的 Job：读取到的 Job 的 `ProjectId` 与工具的 Project 不同时抛出 `JobNotFound`。写操作沿用 `RequireInteractiveUserId` 和删除确认。
- `agw_job_update`：参数全部省略时抛出 `NoChangesToMake`；`name`、`instructions` 提供时不能为空白；`targetKind` 和 `agentId` 必须一起提供；`kind`、`at`、`interval`、`cron` 中提供的内容覆盖已有 `schedule` 的同名字段，四个参数都省略时 `schedule` 保持不变。
- `JobManagementSkill` 的说明中移除 `clearPrompt` 一行，目标一行改为 `When changing the target, provide both targetKind and agentId.`，最后一行改为 `Report the returned job ID and dueAt. Never claim a write succeeded without a successful tool result.`。`job-trigger-reference` 的内容：

```text
# Job schedule reference

All schedules are evaluated in UTC.

- Once: set kind to `Once` and `at` to an RFC 3339 timestamp, for example `2026-08-01T09:00:00Z`. A time that is not in the future runs once immediately.
- Interval: set kind to `Interval` and `interval` to a positive ISO 8601 duration, for example `PT15M`.
- Cron: set kind to `Cron` and `cron` to a standard five-field cron expression, for example `0 9 * * 1-5`.

targetKind values are `Agent` and `Agentflow`.
```

- [x] **步骤 1：编写失败的测试**

`JobsApiTests` 的 fixture：`WebApplication.CreateBuilder()` 加 `UseTestServer()`，调用 `AddApiResult()` 和 `ModuleCompositionTestHost.ConfigureServices`，移除全部 `IHostedService` 登记，`app.UseMiddleware<AgwApiExceptionMiddleware>()`，在中间件中通过 `UserInfoUtil.Push` 设置用户，映射 `MapJobsApi()`，启动之前调用 `ModuleCompositionTestHost.InitializeAsync`。种子数据写入用户 `tester` 和 `other` 各自的 `Project`（`Id`、`Name`、`Workspace`）和 `Agent`（`Id`、`Name`、`DisplayName`），写法见 `TaskPilotRuntimeBehaviorTests.SeedActualHostTargetsAsync`。

```csharp
// EndpointExtensionTests
[Fact]
public async Task MapJobsApi_MapsExpectedRoutes()
Assert.Equal(
    ["api/jobs/create", "api/jobs/delete", "api/jobs/enable", "api/jobs/get", "api/jobs/list", "api/jobs/runs/list", "api/jobs/update"],
    patterns.Order(StringComparer.Ordinal)
);

// JobsApiTests
[Fact]
public async Task Create_CronJob_ReturnsEnvelopeWithDueAt()
Assert.Equal(0, body.GetProperty("code").GetInt32());
Assert.Equal("Cron", data.GetProperty("schedule").GetProperty("kind").GetString());
Assert.Equal(0, data.GetProperty("targetKind").GetInt32());
Assert.Equal(JsonValueKind.String, data.GetProperty("dueAt").ValueKind);
Assert.Equal(JsonValueKind.Null, data.GetProperty("lastDelivery").ValueKind);

[Theory]
[InlineData("target-kind-out-of-range")]   // 复核重点 4，targetKind = 2
[InlineData("missing-schedule")]           // 复核重点 4
[InlineData("blank-instructions")]
[InlineData("agent-of-other-user")]
public async Task Create_InvalidRequest_ReturnsInvalidParamAndSavesNothing(string request)
Assert.Equal(ErrorCodes.InvalidParam.Code, body.GetProperty("code").GetInt32());
Assert.Empty(await db.EventSources.ToListAsync(ct));

[Fact]
public async Task Create_ProjectOfOtherUser_ReturnsResourceNotFound()

[Theory]
[InlineData("get")]
[InlineData("update")]
[InlineData("enable")]
[InlineData("delete")]
[InlineData("runs")]
public async Task Request_OtherUsersJob_ReturnsJobNotFound(string operation)
Assert.Equal(ErrorCodes.JobNotFound.Code, body.GetProperty("code").GetInt32());

[Fact]
public async Task List_ProjectFilter_ReturnsOnlyThatProject()

[Fact]
public async Task Enable_DisableThenEnable_ReturnsDueAtNullThenDueAt()

[Fact]
public async Task Delete_ExistingJob_ReturnsJobAndGetReturnsJobNotFound()

[Theory]
[InlineData(-1, 10)]
[InlineData(0, 0)]
[InlineData(0, 501)]
public async Task RunsList_OffsetOrLimitOutOfRange_ReturnsInvalidParam(int offset, int limit)   // 复核重点 5

// 创建 at 早于当前时间的 Once Job，运行计划、派发、处理投递（投递进入 WaitingExecution），
// 再通过 EventDeliveryManagementAppService.CancelAsync 取消投递
[Fact]
public async Task RunsList_CancelledDelivery_ReturnsFailedRunWithConversationId()
Assert.Equal(2, runs[0].GetProperty("result").GetInt32());
Assert.Equal(conversation.Id.ToString(), runs[0].GetProperty("conversationId").GetString());
Assert.Equal(job.Name, conversation.Title);

// JobAppServiceTests（ModuleCompositionTestHost）
[Fact]
public async Task CreateAsync_BlankName_GeneratesCountBasedUtcName()
Assert.Equal($"job-2-{timeProvider.GetUtcNow():yyyyMMdd}", created.Name);   // 已有一个 Job
[Fact]
public async Task CreateAsync_ProvidedName_TrimsAndPreservesName()
[Fact]
public async Task UpdateAsync_BlankName_GeneratesCountBasedUtcName()

// JobManagementSkillTests（ModuleCompositionTestHost，沿用文件中 PushInteractiveContext 的写法）
[Fact]
public async Task CreateJobAsync_IntervalJob_ReturnsScheduleAndDueAt()
[Fact]
public async Task UpdateJobAsync_NoFields_ThrowsNoChangesToMake()
[Fact]
public async Task UpdateJobAsync_OnlyCron_ReplacesCronAndKeepsOtherFields()
[Fact]
public async Task GetJobAsync_JobInOtherProject_ThrowsJobNotFound()
[Fact]
public async Task DeleteJobAsync_ConfirmationMismatch_ThrowsInvalidParam()

// JobsPostgresTests（[Trait("Category", "Postgres")]，数据库由 PostgresJobTestDatabase 创建，
// 通过 settings 把 Database:Provider 和 Database:ConnectionString 交给 ModuleCompositionTestHost）
// 在 IEventJobFacade.UpdateAsync 读取 Job 后提交 Project 删除。
[Fact]
public async Task Update_ProjectDeletedAfterJobRead_ThrowsConflictAndLeavesNoJobRecords()
Assert.Equal(ErrorCodes.JobModificationConflict.Code, exception.Code);
Assert.False(await db.EventSubscriptions.AnyAsync(row => row.ProjectId == projectId, ct));
Assert.False(await db.EventSources.AnyAsync(row => row.ManagedKey == jobKey && row.DeletedAt == null, ct));
Assert.False(await db.EventSchedules.AnyAsync(row => row.CallerKey == callerKey && row.State == EventScheduleState.Active, ct));

// JobsApiTests。更新接口读取 Job 后删除当前 Project，覆盖原 Project 更新与移动到其他 Project。
[Theory]
[InlineData(false)]
[InlineData(true)]
public async Task Update_ProjectDeletedAfterJobRead_ReturnsConflict(bool moveJob)
Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

[Fact]
public async Task Enable_ConcurrentRequests_KeepTimerSubscriptionAndScheduleConsistent()
// 同时发出 16 个交替的启用和停用请求之后
Assert.Equal(source.Enabled, subscription.Enabled);
Assert.Equal(source.Enabled, schedule.State == EventScheduleState.Active);
```

`HostModuleCompositionTests`：

```csharp
// 取代 JobRegistration_DisabledScheduler_DoesNotRegisterJobHostedService 和 JobRegistration_DurableScheduler_UsesTopologyNeutralExecutor
[Fact]
public void AddJobs_Registration_AddsNoHostedService()
Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));

// ControlPlaneModule_DoesNotMapExecutionOrA2ARoutes
Assert.Contains("api/jobs/list", patterns);
// DataPlaneModule_MapsOnlyExecutionAndA2ARoutes
Assert.DoesNotContain("api/jobs/list", patterns);
```

`AddControlPlaneTestDependencies` 移除 `JobSchedulerWakeSignal`、`IJobsDbContext`、`IJobDeletionCoordinator` 的登记和对应的 using，增加 `IEventJobFacade` 的登记，实现放在文件末尾，写法与已有的 `EmptyProjectTaskFacade` 相同（方法抛出 `NotSupportedException`），这个用例只检查路由。

`DashboardControllerTests`：`CreateController` 改为从 `ModuleCompositionTestHost` 的作用域创建（`ActivatorUtilities.CreateInstance<DashboardController>`），使用它的三个用例改为使用这个作用域的 `AgwDbContext`；增加：

```csharp
// 种子：当前用户两个 Job（其中一个已经删除），另一个用户一个 Job
[Fact]
public async Task GetStats_UserHasJobs_ReturnsOwnJobCount()
Assert.Equal(1, stats.JobCount);
```

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Jobs.Tests`
预期：编译失败，`JobResponse` 等类型不存在。

- [x] **步骤 3：实现**

按照“接口”和“规则”实现，`AddJobs` 的全部调用改为没有参数的写法。

- [x] **步骤 4：更新 PostgreSQL CI 清单**

`.github/scripts/verify-postgres-tests.py`：

```python
job_tests = (
    "Update_ConcurrentProjectDeletion_LeavesNoJobRecordsOfThatProject",
    "Enable_ConcurrentRequests_KeepTimerSubscriptionAndScheduleConsistent",
)
```

`.github/workflows/build-server.yml` 中对应步骤的名称改为 `Test PostgreSQL job writes`。

- [x] **步骤 5：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Jobs.Tests`、`dotnet test --project tests/Agw.Host.Tests --filter-class "*HostModuleCompositionTests"`、`dotnet test --project tests/Agw.Host.Tests --filter-class "*DashboardControllerTests"`、`dotnet test --project tests/Agw.Agents.Tests --filter-class "*SkillToolCompositionTests"`、`dotnet test --project tests/Agw.Setup.Tests`、`dotnet test --project tests/Agw.Architecture.Tests`
PostgreSQL 用例（连接配置在 `.scratch/postgres-tests.json`）：`dotnet build tests/Agw.Jobs.Tests -c Release`，再运行 `tests/Agw.Jobs.Tests/bin/Release/net10.0/Agw.Jobs.Tests -filterVSTest "Category=Postgres" -result-trx TestResults/Postgres/jobs.trx -failSkips` 和 `python3 .github/scripts/verify-postgres-tests.py TestResults/Postgres --suite jobs`
预期：全部通过。

- [ ] **步骤 6：提交**

`git commit -m "feat(jobs): serve jobs through the event job facade"`

---

### 任务 6：移除旧调度、持久化和 Job 实体

**文件：**
- 删除：`src/server/Agw.Jobs/Scheduling`、`Execution`、`Domain/Behaviors`、`Domain/ValueObjects`、`Application/Persistence`
- 删除：`src/server/Agw.Infrastructure/Jobs`、`src/server/Agw.Infrastructure/Repositories/JobRepo.cs`
- 删除：`src/server/Agw.Data/Entities/Jobs`
- 修改：`src/server/Agw.Jobs/Agw.Jobs.csproj`（移除 `Cronos`）
- 修改：`src/server/Agw.Infrastructure/Agw.Infrastructure.csproj`（移除 `Agw.Jobs` 引用）、`src/server/Agw.Host/Agw.Host.csproj`（增加 `Agw.Jobs` 引用）
- 修改：`src/server/Agw.Infrastructure/DependencyInjection.cs`（移除 Jobs 的登记和 using）
- 修改：`src/server/Agw.Infrastructure/Data/AgwDbContext.cs`（移除 `IJobsDbContext`、`Jobs`、`JobLogs`、`StampJobRowVersions`）、`UserScopeModelBuilderExtensions.cs`（移除 `Job`、`JobLog` 的筛选器）、`Interceptors/EntityOwnerGuard.cs`（移除 `JobLog` 的分支）
- 修改：`src/server/Agw.Infrastructure/Projects/ProjectDeletionCoordinator.cs`（移除 `DeleteJobsAsync`）、`Agents/AgentDeletionCoordinator.cs`（移除 `job` 表的检查）
- 修改：`src/server/Agw.Shared/Exceptions/ErrorCodes.cs`（移除 `InvalidOnceTriggerValue`、`InvalidIntervalTriggerValue`、`InvalidCronTriggerValue`、`UnsupportedTriggerType`、`JobAgentTargetRequired`、`JobActiveAttemptConflict`）
- 修改：`src/server/Agw.Agents.Contracts/Execution/AgentExecutionContracts.cs`（移除 `IAgentExecutionFacade.ExecuteAsync`）、`src/server/Agw.Agents.Execution/Inbound/Facades/AgentExecutionFacade.cs`（移除它的实现）
- 旧表删除纳入任务 7 的 `MoveJobsToEventTimers` migration。
- 修改：`tests/Agw.Architecture.Tests/BackendArchitectureTests.cs`（项目引用清单：`Agw.Infrastructure` 移除 `Agw.Jobs`，`Agw.Host` 增加 `Agw.Jobs`；`EntityOwners` 移除 `Job`、`JobLog`）
- 修改：`tests/Agw.Architecture.Tests/PersistenceOwnershipArchitectureTests.cs`（`ModuleDbContextOwners` 和 `ModuleDbContextReferenceRegex` 移除 `IJobsDbContext`；两个例外清单移除 `JobRepo.cs` 和 `JobAttemptOutcomeRecorder.cs`）
- 修改：`tests/Agw.Files.Tests/Agw.Files.Tests.csproj`、`tests/Agw.Setup.Tests/Agw.Setup.Tests.csproj`（增加 `Agw.Jobs` 引用）、`tests/Agw.Projects.Tests/Agw.Projects.Tests.csproj`（移除 `Agw.Jobs` 引用）
- 删除测试：`tests/Agw.Projects.Tests/JobRowVersionTests.cs`、`AgentExecutorTests.cs`
- 修改测试：
  - `tests/Agw.Projects.Tests/ProjectDeletionCoordinatorTests.cs`：移除 `DeleteProjectAsync_RunningJobExists_ThrowsConflictAndRollsBack`、`DeleteProjectAsync_JobDeleteLosesRace_ThrowsConflictAndRollsBack`，以及其他用例中 `Job`、`JobLog` 的种子数据和断言
  - `tests/Agw.Projects.Tests/AgentDeletionCoordinatorTests.cs`：移除 `enabled-job`、`disabled-job` 两个取值
  - `tests/Agw.Projects.Tests/Infrastructure/UserScopeFilterTests.cs`：移除 `Job`、`JobLog` 的清单条目、种子数据和断言
  - `tests/Agw.Projects.Tests/Infrastructure/EntityTypeConfigurationTests.cs`：`JobAndIntegrationEntities_DeclareMatchingConfigurations` 移除 `Job`、`JobLog`，名称改为 `IntegrationEntities_DeclareMatchingConfigurations`
  - `tests/Agw.Integrations.Tests/EntityAuditInterceptorTests.cs`：移除 `SaveChanges_JobLogOwnedByAnotherUser_IsAllowedOnCreateAndRejectedOnUpdate` 和测试上下文中的 `JobLogs`
  - `tests/Agw.Jobs.Tests/InfrastructureRegistrationTests.cs`：移除 `AddInfrastructure_RegistersProjectExecutionLockRouter`，其他用例移除 Jobs 的类型
  - `tests/Agw.A2A.Tests/A2ADependencyInjectionTests.cs`：`FakeAgentExecutionFacade` 移除 `ExecuteAsync`
  - `tests/Agw.Agents.Tests/AgentExecutionFacadeTests.cs`：`ExecuteAsync_InProcess_RunsTurnAndRestoresUserContext`、`ExecuteAsync_InProcessAllowPolicy_StillUsesUnattendedRules` 改为调用 `ExecuteStreamingAsync` 并读取到结束，名称的前缀改为 `ExecuteStreamingAsync_`；对 `result.State` 的断言改为结束消息的 `status` 为 `completed`，对 `result.Messages` 的断言改为对读取到的消息断言
  - `tests/Agw.Agents.Tests/DurableAgentExecutionFacadeTests.cs`：`FacadeExecuteAsync_Distributed_AcceptsTurnAndReturnsCommittedOutcome` 按照同样的方式修改，名称改为 `FacadeExecuteStreamingAsync_Distributed_AcceptsTurnAndReturnsCommittedOutcome`

**接口：**
- 使用：任务 4、任务 5 的结果
- 产出：模型和代码中没有 `Job`、`JobLog`、`IJobsDbContext`、`IProjectExecutionLock`；`IAgentExecutionFacade` 只有 `ExecuteStreamingAsync`

- [x] **步骤 1：删除文件并修改引用它们的代码**

按照“文件”一节执行。

- [x] **步骤 2：构建**

运行：`dotnet build Agw.slnx`
预期：构建成功。出现的编译错误都来自对已删除类型的引用，逐个移除。

- [x] **步骤 3：检查旧表删除操作**

检查 `MoveJobsToEventTimers.Up` 删除 `job_log` 和 `job` 两张表。

- [x] **步骤 4：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Architecture.Tests`、`tests/Agw.Shared.Tests`、`tests/Agw.Jobs.Tests`、`tests/Agw.Projects.Tests`、`tests/Agw.Events.Tests`、`tests/Agw.Host.Tests`、`tests/Agw.Agents.Tests`、`tests/Agw.A2A.Tests`、`tests/Agw.Files.Tests`、`tests/Agw.Integrations.Tests`、`tests/Agw.Setup.Tests`、`tests/Agw.Settings.Tests`
预期：全部通过。

- [x] **步骤 5：确认没有残留引用**

运行：`git grep -n -e JobHostedService -e IProjectExecutionLock -e IJobsDbContext -e JobBehavior -e JobLogs -e "Entities.Jobs" -- src/server tests ':!src/server/Agw.Migrations.*' ':!*.md'`
预期：没有输出。

- [ ] **步骤 6：提交**

`git commit -m "refactor(jobs): remove the scheduler, job persistence and job entities"`

---

### 任务 7：会话类型和关联 ID

**文件：**
- 修改：`src/server/Agw.Data/Entities/Projects/ProjectConversation.cs`、`ProjectConversationConfiguration.cs`、`ProjectConversationChatHistory.cs`、`ProjectConversationChatHistoryConfiguration.cs`
- 修改：`src/server/Agw.Projects.Contracts/Execution/ProjectTaskContracts.cs`
- 修改：`src/server/Agw.Projects/Contracts/TaskRequests.cs`（`TaskCreateRequest`、`ProjectConversationSummaryResponse`、`ProjectConversationResponse`）、`Contracts/TaskExecutionSnapshots.cs`
- 修改：`src/server/Agw.Projects/Application/TaskProjection.cs`、`TaskExecutionMapper.cs`、`TaskExecutionAppService.cs`、`TaskAppService.cs`、`Facades/ProjectTaskFacade.cs`、`ProjectConversationAppService.cs`
- 修改：`src/server/Agw.Projects/Domain/Behaviors/ProjectConversationBehavior.cs`、`Domain/Services/ConversationHistoryDomainService.cs`
- 修改：`src/server/Agw.Agents.Execution/Inbound/Facades/BackgroundExecutionFacade.cs`、`AgentExecutionTask.cs`、`Runtimes/ProjectTaskProjectionMapper.cs`
- 修改：`src/server/Agw.A2A/A2AAgentExecutionBridge.cs:91-94`
- 创建：`src/server/Agw.Migrations.Sqlite/Migrations/*_MoveJobsToEventTimers.cs`、`src/server/Agw.Migrations.Postgres/Migrations/*_MoveJobsToEventTimers.cs`（`dotnet ef` 生成）
- 修改测试：`tests/Agw.Projects.Tests/ProjectConversationBehaviorTests.cs`、`TaskExecutionAppServiceTests.cs`、`TaskConversationCreatorTests.cs`、`TaskAppServiceTests.cs`、`ProjectConversationAppServiceTests.cs`、`ConversationHistoryDomainServiceTests.cs`、`tests/Agw.Events.Tests/EventDeliveryIntegrationTests.cs`、`tests/Agw.Agents.Tests/DurableAgentExecutionFacadeTests.cs`（`ProjectTaskSnapshot` 的参数）
- 修改测试中的 `IProjectTaskFacade` 实现：`tests/Agw.A2A.Tests/A2ADependencyInjectionTests.cs`、`tests/Agw.Agents.Tests/ExecutionCommandHandlerTests.cs`、`ExecutionConnectionTests.cs`、`tests/Agw.Host.Tests/HostModuleCompositionTests.cs`
- 创建测试：`tests/Agw.Projects.Tests/Infrastructure/JobsEventTimerMigrationTests.cs`

**接口：**
- 使用：任务 1 的 `ConversationKinds`；已有的 `IEventContextFacade`、`IEventSourceFacade`
- 产出：
  - `ProjectConversation.Kind`（`string`，默认 `ConversationKinds.Native`，`IsRequired().HasMaxLength(64).HasDefaultValue(ConversationKinds.Native)`）、`ProjectConversation.RelateId`（`Guid?`，带有索引）；移除 `ProjectConversation.JobId` 和 `ProjectConversationChatHistory.JobId`
  - `StartProjectTaskRequest(Guid ProjectId, Guid TaskId, string Input, string? Title, string? ContextId, string OwnerUserId, ProjectTaskStatus InitialStatus, string? RequesterExtensionId = null, string Kind = ConversationKinds.Native, Guid? RelateId = null)`
  - `TaskCreateRequest(string Input, string? Title = null, string? ContextId = null, string Kind = ConversationKinds.Native, Guid? RelateId = null)`
  - `void ProjectConversationBehavior.SetOrigin(string kind, Guid? relateId)`，取代 `TryBindJob`
  - `ProjectConversationSummaryResponse` 和 `ProjectConversationResponse` 中 `Guid? JobId` 的位置改为 `string Kind, Guid? RelateId`
  - 移除：`ProjectTaskSnapshot.JobId`、`TaskProjection.JobId`、`TaskExecutionSnapshots.cs` 中两个记录的 `JobId`、`AgentExecutionTask.JobId`
  - 移除：`IProjectTaskFacade.FinishAsync`、`FinishProjectTaskRequest`，以及随之没有调用方的 `TaskExecutionAppService.MarkSucceededAsync`、`MarkFailedAsync`、`MarkTaskAsync` 和 `ConversationHistoryDomainService.TryFinishTask`

**规则：**
- `SetOrigin`：`kind` 为 `native` 并且 `relateId` 不为空、`ConversationKinds.IsEvent(kind)` 并且 `relateId` 为空、`kind` 是其他内容，这三种情况抛出 `AgwException(ErrorCodes.InvalidParam)`。
- `kind` 和 `relate_id` 只在创建会话时写入（`TaskExecutionAppService.GetOrCreateConversationAsync` 新建会话的分支调用 `SetOrigin`）。在已有会话中创建任务时保持不变。
- `BackgroundExecutionFacade` 的构造函数增加 `IEventSourceFacade`。`SubmitAsync` 中调用方是事件投递（`caller.RequesterExtensionId` 为空）时：`request.ParentEventId` 为空则抛出 `InvalidParam`；通过 `IEventContextFacade.GetAsync` 取得 `SourceId`，再通过 `IEventSourceFacade.GetAsync` 取得 `Kind`，任何一个返回 `null` 时抛出 `ResourceNotFound`；向 `StartProjectTaskRequest` 传入 `ConversationKinds.ForEventSource(kind)` 和 `ParentEventId`。extension 调用方使用默认值。
- `ProjectConversationAppService.ListResponsesAsync` 的筛选条件：第一个分支由 `conversation.JobId == null` 改为 `conversation.Kind == ConversationKinds.Native`，其余两个分支保持不变。

- [x] **步骤 1：编写失败的测试**

```csharp
// ProjectConversationBehaviorTests（取代 TryBindJob_ConversationAlreadyBound_KeepsOriginalJob）
[Fact]
public void SetOrigin_EventKindWithRelateId_WritesKindAndRelateId()
[Theory]
[InlineData("native", true)]
[InlineData("event:timer", false)]
[InlineData("event:unknown", true)]
[InlineData("timer", false)]
public void SetOrigin_InvalidCombination_ThrowsInvalidParam(string kind, bool withRelateId)

// TaskExecutionAppServiceTests
// 移除 MarkSucceededAsync_AfterCreateRunningAsync_UpdatesProjectConversationChatHistory 和 MarkTaskAsync_RunningTask_PreservesConversationAudit
// CreateRunningForExecutionAsync_RecordsRequesterExtensionAsConversationCreator 的末尾增加
Assert.Equal(ConversationKinds.Native, conversation.Kind);
Assert.Null(conversation.RelateId);
[Fact]
public async Task CreateRunningForExecutionAsync_EventKind_CreatesConversationWithKindAndRelateId()
Assert.Equal("event:timer", conversation.Kind);
Assert.Equal(eventId, conversation.RelateId);
// 第二个请求带有 Kind = "event:timer" 和 RelateId，使用第一个请求创建的 native 会话
[Fact]
public async Task CreateRunningForExecutionAsync_ExistingConversation_KeepsKindAndRelateId()
Assert.Equal(ConversationKinds.Native, conversation.Kind);
Assert.Null(conversation.RelateId);

// TaskConversationCreatorTests：ResolveTaskAsync_NewConversationWithoutExtension_RecordsHostCreator 的末尾增加
Assert.Equal(ConversationKinds.Native, conversation.Kind);

// ConversationHistoryDomainServiceTests：移除三个 TryFinishTask 的用例

// ProjectConversationAppServiceTests
// ListResponsesAsync_InteractiveConversationWithoutMessages_IsIncluded：三个 Job 会话的种子改为 Kind = "event:timer"、RelateId = eventId，断言保持不变
// ListResponsesAsync_GroupsTasksFromRecordsByContext 和读取单个会话的用例中 Assert.Equal(jobId, context.JobId) 改为
Assert.Equal("event:timer", context.Kind);
Assert.Equal(eventId, context.RelateId);

// EventDeliveryIntegrationTests：种子写入当前用户的 Agent 和 Project（写法见 TaskPilotRuntimeBehaviorTests.SeedActualHostTargetsAsync）
[Fact]
public async Task ProcessAsync_AgentTarget_CreatesEventConversationRelatedToEvent()
Assert.Equal(EventDeliveryState.WaitingExecution, delivery.State);
Assert.Equal(ConversationKinds.ForEventSource(source.Kind), conversation.Kind);
Assert.Equal(eventId, conversation.RelateId);
```

`JobsEventTimerMigrationTests` 的结构与 `ConversationCreatorExtensionMigrationTests` 相同。迁移到 `AddEventCenterAndNotifications` 之后写入一条带有 `job_id` 的会话、一条 `job` 和一条 `job_log`，再迁移到最新：

```csharp
[Fact]
public async Task Upgrade_Sqlite_RemovesJobTablesAndAddsConversationKind()
[Fact(SkipUnless = nameof(PostgresEnabled), Skip = "Requires an isolated PostgreSQL test database with CREATE DATABASE permission.")]
public async Task Upgrade_Postgres_RemovesJobTablesAndAddsConversationKind()
// 断言
Assert.Equal("native", kind);
Assert.Null(relateId);
Assert.False(await TableExistsAsync("job"));
Assert.False(await TableExistsAsync("job_log"));
Assert.False(await ColumnExistsAsync("project_conversation", "job_id"));
Assert.False(await ColumnExistsAsync("project_conversation_chat_history", "job_id"));
Assert.True(await ColumnExistsAsync("event_source", "managed_by"));
Assert.True(await ColumnExistsAsync("event_source", "managed_key"));
Assert.True(await ColumnExistsAsync("event_subscription", "managed_by"));
Assert.False(await ColumnExistsAsync("event_subscription", "managed_by_extension_id"));
// 种子中 managed_by_extension_id 有值的订阅
Assert.Equal("ext:019a4c17-0000-7000-8000-00000000abcd", managedBy);
Assert.True(await ColumnExistsAsync("event_delivery_attempt", "turn_id"));
```

- [x] **步骤 2：运行测试，确认失败**

运行：`dotnet test --project tests/Agw.Projects.Tests --filter-class "*ProjectConversationBehaviorTests"`
预期：编译失败，`SetOrigin` 不存在。

- [x] **步骤 3：实现实体、接口和规则**

按照“接口”和“规则”实现。

- [x] **步骤 4：生成 migration**

向用户确认之后执行：

```bash
dotnet ef migrations add MoveJobsToEventTimers -p src/server/Agw.Migrations.Sqlite -s src/server/Agw.Standalone.Host -- --provider sqlite
dotnet ef migrations add MoveJobsToEventTimers -p src/server/Agw.Migrations.Postgres -s src/server/Agw.Standalone.Host -- --provider postgres
```

检查两种数据库对应的 `Up` 操作：

- `project_conversation` 增加 `kind`（不为空，默认 `native`）；`job_id` 通过 `RenameColumn` 改为 `relate_id`，索引通过 `RenameIndex` 改名。生成结果是删除再新增时，使用 Edit 工具改为 `RenameColumn` 和 `RenameIndex`。
- 在 `RenameColumn` 之后清空原先带有 Job ID 的 `relate_id`。
- `project_conversation_chat_history` 删除 `job_id`。
- 同一次升级新增任务 2 的 Events 列和索引，并删除任务 6 的旧表。由 `AddEventCenterAndNotifications` 升级后只新增一条 migration 记录。

- [x] **步骤 5：运行测试，确认通过**

运行：`dotnet test --project tests/Agw.Projects.Tests`、`tests/Agw.Events.Tests`、`tests/Agw.Agents.Tests`、`tests/Agw.A2A.Tests`、`tests/Agw.Host.Tests`、`tests/Agw.Jobs.Tests`、`tests/Agw.Settings.Tests`、`tests/Agw.Architecture.Tests`
PostgreSQL 的 migration 用例：`AGW_TEST_POSTGRES_CONNECTION_STRING="Host=localhost;Database=postgres;Username=postgres;Pooling=false" dotnet test --project tests/Agw.Projects.Tests --filter-class "*JobsEventTimerMigrationTests"`
预期：全部通过。

- [ ] **步骤 6：提交**

`git commit -m "feat(projects): record conversation kind and related event"`

---

### 任务 8：客户端的 API 类型和 Jobs 页面

**文件：**
- 修改：`src/clients/packages/api/openapi.json`、`src/clients/packages/api/src/openapi.d.ts`（生成）
- 创建：`src/clients/packages/jobs/src/ui-web/pages/jobs/schedule-value.ts`、`schedule-value.test.ts`
- 删除：`src/clients/packages/jobs/src/ui-web/pages/jobs/trigger-value.ts`、`trigger-value.test.ts`
- 修改：`src/clients/packages/jobs/src/ui-web/pages/jobs/page.tsx`、`logs/page.tsx`
- 修改：`src/clients/desktop/renderer/src/runtime/components/workspace/jobs-workspace.test.ts`

**接口：**
- 使用：任务 5 的路由和字段
- 产出（`schedule-value.ts`）：

```ts
export const SCHEDULE_KIND_ONCE = "Once";
export const SCHEDULE_KIND_INTERVAL = "Interval";
export const SCHEDULE_KIND_CRON = "Cron";
export type JobSchedule = { kind: string; at?: string | null; interval?: string | null; cron?: string | null };
export function isPositiveIsoDuration(value?: string | null): value is string;
export function isCronValue(value?: string | null): value is string;
export function getScheduleError(schedule: JobSchedule): string | null;
export function getDeliveryStateLabel(state: number): string;
```

`getDeliveryStateLabel` 的对应关系：`0`、`1`、`2`、`5` 为 `Running`，`3` 为 `Succeeded`，`4` 为 `Retrying`，`6` 为 `Failed`，`7` 为 `Cancelled`。

- [x] **步骤 1：导出 OpenAPI 并生成类型**

把下列内容写入 `.scratch/jobs-openapi/export.sh` 之后运行。`/openapi/v1.json` 只在 Development 环境映射，数据库和数据目录都指向 `.scratch/jobs-openapi`：

```bash
#!/bin/bash
set -euo pipefail
root="$(git rev-parse --show-toplevel)"
work="$root/.scratch/jobs-openapi"
mkdir -p "$work"
dotnet build "$root/src/server/Agw.Standalone.Host"
dotnet "$root/src/server/Agw.Standalone.Host/bin/Debug/net10.0/agw-server.dll" \
  --environment=Development --urls=http://127.0.0.1:30918 \
  --AgwDataDir="$work/data" --AgwLogDir="$work/logs" \
  --Database:Provider=sqlite "--Database:ConnectionString=Data Source=$work/agw.db" \
  --Execution:Provider=InProcess --DistributedLock:Provider=inmemory &
pid=$!
trap 'kill "$pid"' EXIT
curl --retry 60 --retry-connrefused --retry-delay 1 -fsS http://127.0.0.1:30918/openapi/v1.json \
  -o "$root/src/clients/packages/api/openapi.json"
```

然后在 `src/clients` 下运行 `pnpm gen:api`。
预期：`openapi.d.ts` 中出现 `JobResponse`、`JobRunResponse` 和 `/api/jobs/list` 等路径；`EventSourceResponse`、`EventSubscriptionResponse` 带有 `managedBy` 和 `managedKey`；会话响应带有 `kind` 和 `relateId`。

- [x] **步骤 2：编写失败的测试**

```ts
test("isPositiveIsoDuration accepts positive ISO 8601 durations", () => {
  for (const value of ["PT1M", "PT15M", "PT1H30M", "P1D", "P1DT2H", "P1M", "PT0.5S"]) {
    assert.equal(isPositiveIsoDuration(value), true, value);
  }
});

test("isPositiveIsoDuration rejects zero, .NET TimeSpan and malformed values", () => {
  for (const value of ["PT0S", "P0D", "00:15:00", "P", "PT", "15M", "", null]) {
    assert.equal(isPositiveIsoDuration(value), false, String(value));
  }
});

test("getScheduleError requires the field of the selected kind", () => {
  assert.equal(getScheduleError({ kind: "Once", at: "2026-08-01T09:00:00Z" }), null);
  assert.notEqual(getScheduleError({ kind: "Once" }), null);
  assert.notEqual(getScheduleError({ kind: "Interval", interval: "00:15:00" }), null);
  assert.notEqual(getScheduleError({ kind: "Cron", cron: "* * *" }), null);
});

test("getDeliveryStateLabel maps event delivery states", () => {
  assert.deepEqual(
    [0, 1, 2, 3, 4, 5, 6, 7].map(getDeliveryStateLabel),
    ["Running", "Running", "Running", "Succeeded", "Retrying", "Running", "Failed", "Cancelled"],
  );
});
```

`jobs-workspace.test.ts` 的数据改为新的字段，查询键和界面断言保持不变：

```ts
const job = {
  id: "job-1", name: "Scheduled work", enabled: true, projectId: "project-1", targetKind: 0, agentId: "agent-1",
  instructions: "Run", schedule: { kind: "Interval", interval: "PT5M" }, dueAt: "2026-11-08T10:00:00Z",
  scheduleCompleted: false, lastDelivery: null,
};
client.setQueryData(["job-logs", job.id], [
  { id: "run-1", deliveryId: "delivery-1", attempt: 1, startedAt: "2026-11-08T09:55:00Z",
    endedAt: "2026-11-08T09:55:01Z", result: 0, error: null, conversationId: "conversation-1" },
]);
```

- [x] **步骤 3：运行测试，确认失败**

运行（在 `src/clients` 下）：`pnpm --filter @agw/jobs exec tsx --test src/ui-web/pages/jobs/schedule-value.test.ts`
预期：失败，模块不存在。

- [x] **步骤 4：实现 `schedule-value.ts` 并修改页面**

`page.tsx`（查询键、页面组件的 props 和按钮名称保持不变）：
- 接口路径改为 `/api/jobs/list`、`get`、`create`、`update`、`enable`、`delete`，请求和响应使用任务 5 的字段。
- 表单字段：名称（可以为空）、Project、执行目标（必填）、指令（必填）、`schedule` 的类型和对应的输入（`Once` 使用 `DateTimePicker`，`Interval` 输入 ISO 8601 时长，默认 `PT1M`，`Cron` 输入表达式，默认 `*/1 * * * *`）、启用开关。移除 Max Retry Count 和 Status。
- 列表的列：Name、Project / Agent、Schedule、Next Run、Enabled、Last Delivery、Actions。Once Job 的 `scheduleCompleted` 为 `true` 时，Next Run 显示 `Ended`。有 `dueAt` 的启用 Job 显示时间，其余显示 `-`。Last Delivery 显示 `getDeliveryStateLabel(lastDelivery.state)`、`Attempt {attempt}` 和 `lastError`，没有投递时显示 `-`。
- Job 详情和 Job Logs 使用 `offset`、`limit` 分页读取运行历史，每页显示 20 条。

`logs/page.tsx`：
- 请求 `/api/jobs/get?id=` 和 `/api/jobs/runs/list?jobId=`。列：Result（`0` 为 `Succeeded`，`1` 为 `Retry`，`2` 为 `Failed`）、Attempt、Delivery ID、Time（`startedAt` 到 `endedAt`）、Error、Actions（Go to Chat 使用 `conversationId`，为空时禁用）。

- [x] **步骤 5：运行测试，确认通过**

运行（在 `src/clients` 下）：`pnpm --filter @agw/jobs test`、`pnpm exec turbo run typecheck lint --filter=@agw/jobs`、`pnpm --filter @agw/desktop test`
预期：全部通过。

- [ ] **步骤 6：提交**

`git commit -m "feat(jobs-ui): use the events-shaped jobs api"`

---

### 任务 9：客户端的 Events 页面、Create Task 和会话类型

**文件：**
- 修改：`src/clients/packages/events/src/ui-web/pages/events-page.tsx`
- 创建：`src/clients/packages/events/src/ui-web/pages/managed-record.ts`、`managed-record.test.ts`
- 修改：`src/clients/packages/projects/src/ui-web/pages/projects/details/project-details.ts`、`page.tsx`
- 创建：`src/clients/packages/projects/src/ui-web/pages/projects/details/project-details.test.ts`
- 修改：`src/clients/packages/projects-core/src/task-client.ts`、`projects-core.test.ts`，`src/clients/packages/projects/src/services/task-client.test.ts`，`src/clients/web/e2e/chat-api.ts`

**接口：**
- 使用：任务 8 生成的类型
- 产出：
  - `CreateTaskJobRequest = { name: string; enabled: boolean; projectId: string; targetKind: 0 | 1; agentId: string; instructions: string; schedule: { kind: "Once"; at: string } }`
  - `ConversationSummary` 和 `ProjectConversationSummaryResponse` 的 `kind: string`、`relateId?: string | null`，取代 `jobId`

- [x] **步骤 1：编写失败的测试**

```ts
// project-details.test.ts
test("buildCreateTaskJobRequest builds a one-time job", () => {
  const now = new Date("2026-11-12T08:00:00Z");
  assert.deepEqual(
    buildCreateTaskJobRequest({ projectId: "p", targetValue: "agent:a", jobName: " task ", prompt: " run ", now }),
    {
      name: "task",
      enabled: true,
      projectId: "p",
      targetKind: 0,
      agentId: "a",
      instructions: "run",
      schedule: { kind: "Once", at: "2026-11-12T08:00:10.000Z" },
    },
  );
});
```

`task-client.test.ts` 和 `projects-core.test.ts` 中的 `jobId` 改为 `kind` 和 `relateId`：

```ts
assert.equal(result.items[1].kind, "event:timer");
assert.equal(result.items[1].relateId, "event-1");
assert.equal(result.items[0].kind, "native");
assert.equal(result.items[0].relateId, null);
```

- [x] **步骤 2：运行测试，确认失败**

运行（在 `src/clients` 下）：`pnpm --filter @agw/projects test`、`pnpm --filter @agw/projects-core test`
预期：失败，请求和会话中是原来的字段。

- [x] **步骤 3：实现**

- `project-details.ts`：`buildCreateTaskJobRequest` 返回新的结构，移除 `QUICK_TASK_TRIGGER_TYPE`；`page.tsx:132` 的路径改为 `/api/jobs/create`。
- `task-client.ts`：两个类型和 `toConversationSummary` 使用 `kind`、`relateId`。
- `web/e2e/chat-api.ts`：会话数据的 `jobId: null` 改为 `kind: "native"`、`relateId: null`。
- `managed-record.ts` 的 `getManagedLabel(managedBy: string, managedKey?: string | null): string | null`：`user` 返回 `null`，`job` 返回 `Managed by Job {managedKey}`，`ext:<extensionId>` 返回 `Managed by extension {extensionId}`。
- `events-page.tsx` 在 `getManagedLabel` 有返回值的来源和订阅上显示这段文字和 `Read only`。Job 的记录在 Jobs 页面修改、删除和启停。

- [x] **步骤 4：运行测试，确认通过**

运行（在 `src/clients` 下）：`pnpm --filter @agw/projects test`、`pnpm --filter @agw/projects-core test`、`pnpm --filter @agw/mobile test`、`pnpm typecheck`、`pnpm lint`、`pnpm test:boundaries`
预期：全部通过。

- [ ] **步骤 5：提交**

`git commit -m "feat(clients): show job-managed event records and conversation kinds"`

---

### 任务 10：文档、全量回归和主流程验证

**文件：**
- 修改：`AGENTS.md`、`CONTEXT.md`、`docs/1.Development.md`、`docs/2.Architecture.md`、`docs/4.Deployment.md`、`src/server/Agw.Agents.Execution/README.md`
- 重写：`src/server/Agw.Jobs/README.zh-CN.md`

**接口：**
- 使用：前面全部任务的结果

- [x] **步骤 1：更新文档**

- `src/server/Agw.Jobs/README.zh-CN.md`：按照新的模块职责、路由、`schedule` 格式、工具参数和执行规则重写，执行规则引用设计文档“触发事件并执行 Agent”。
- `src/server/Agw.Agents.Execution/README.md`：关于 Jobs 调用 `IAgentExecutionFacade`、Control Plane 为 Jobs 登记 durable client、Durable Job 等待结果的描述，改为 Events 的投递 worker 通过 `IBackgroundExecutionFacade` 提交执行；`IAgentExecutionFacade` 的说明只保留 `ExecuteStreamingAsync`。
- `docs/2.Architecture.md`：`Agw.Jobs` 的说明、依赖图中 `Agw.Jobs` 到 `Agw.Events.Contracts`、`Agw.Host` 到 `Agw.Jobs` 的引用、Host Roles 中关于 Job 调度的描述。
- `docs/4.Deployment.md`：Control Plane 的 Events worker 触发 Job。`MoveJobsToEventTimers` 删除 `job` 和 `job_log`，升级后在 Jobs 页面重新创建 Job。
- `docs/1.Development.md`：Jobs 的 PostgreSQL 测试说明（Job 的修改与 Project 删除同时进行、同时启停）。
- `CONTEXT.md`：Control Plane、Durable Execution、Module DbContext 三个条目中关于 Jobs 的描述；增加 Conversation Kind 条目（`native`、`event:<来源类型>` 和 `relate_id`）。
- `AGENTS.md`：Module DbContext 的数量（十个），Hosts 一节中 Control Plane 的职责描述。

- [ ] **步骤 2：全量回归**

运行：
- `dotnet build Agw.slnx`
- `env -u ASPNETCORE_ENVIRONMENT -u ASPNETCORE_URLS dotnet test Agw.slnx`
- `dotnet csharpier check .`
- 在 `src/clients` 下：`pnpm typecheck`、`pnpm lint`、`pnpm test`、`pnpm fmt:check`

预期：全部通过。

全量回归执行了一轮。TaskPilot Runtime 单独运行的 23 项测试通过。Mobile、Chat 和 Desktop 的包内测试分别通过 70、383 和 617 项。审查修复后，Jobs 的 69 项测试、Events 的 189 项测试和架构的 56 项测试通过。SQLite 与 PostgreSQL 的迁移检查均通过。构建、客户端类型检查、lint、客户端格式检查和全仓 CSharpier 检查均通过。

- [x] **步骤 3：Standalone 主流程验证**

集成与组件测试全部通过之后执行。使用独立的数据目录启动 Standalone（`--environment=Production`、`--urls=http://127.0.0.1:30918`、`.scratch/jobs-acceptance` 下的 `AgwDataDir`、`AgwLogDir` 和 SQLite 连接、`--Execution:Provider=InProcess`、`--DistributedLock:Provider=inmemory`、`--Setup:AdminPassword`），模型 Provider 使用本机的 Ollama：

1. 创建一个 Agent，通过 `POST /api/jobs/create` 创建 `schedule = { kind: "Once", at: <当前时间加 5 秒> }` 的 Job。
2. 等待到期，`GET /api/jobs/get` 的 `dueAt` 为空，`scheduleCompleted` 为 `true`，`lastDelivery.state` 为 `3`。
3. `GET /api/jobs/runs/list` 返回一条 `result = 0` 的记录，`conversationId` 不为空。
4. `GET /api/projects/{projectId}/conversations` 中这个会话的 `kind` 为 `event:timer`，`relateId` 等于 `GET /api/events/list` 中 `job.timer.fired` 事件的 `id`。
5. 在 Web 的 Jobs 页面查看列表、运行历史并打开会话；在 Events 页面确认这个 Job 的 Timer 和订阅显示为 `Managed by Job` 并且只读；在 Project 详情页通过 Create Task 创建任务并确认它执行。

- [x] **步骤 4：独立部署验证**

按照 `docs/4.Deployment.md` 启动 Control Plane 和 Data Plane（PostgreSQL、`Execution:Provider=Distributed`）：

1. 通过 Control Plane 的 API 创建 `Interval`（`PT1M`）的 Job，确认到期之后由 Data Plane 执行并产生运行记录；把执行目标换成 Agentflow 再确认一次。
2. 在 Data Plane 执行的会话中让绑定了 `agw-job` 的 Agent 调用 `agw_job_create`，确认 Control Plane 在到期时触发这个 Job。
3. 停用模型 Provider 使执行失败一次，确认投递进入 `RetryScheduled`，恢复之后重试成功，运行历史中两次尝试的 `conversationId` 不同。

- [ ] **步骤 5：提交**

`git commit -m "docs: describe jobs on the event center"`
