# Agw.Jobs 代码重组设计

## 目标

在保留现有外部行为的前提下重新组织 `Agw.Jobs`，提高调度代码的 Locality，让核心状态流可以通过一个深 Module 直接测试，并把当前进行中的 Minimal API 迁移纳入最终目录结构。

本次重组的成功标准：

- `/api/jobs` 的路由、请求和 Bens.Results 响应语义不变；
- Job 创建、预取、项目串行、Agent 执行、重试、暂停和日志语义不变；
- 预取间隔仍为 1 分钟，预取窗口仍为 10 分钟，重试间隔仍为 30 秒；
- `MaxRetryCount` 仍表示首次尝试后的最大重试次数；
- SQLite 使用进程内项目锁，PostgreSQL 可使用分布式项目锁；
- `Agw.Jobs.Tests` 覆盖新目录结构和抽取后的单次执行状态流；
- 不修改数据库实体、数据库结构、迁移、OpenAPI 数据契约或 Web 客户端调用方式。

## 当前问题

### `JobHostedService` 同时包含两个变化轴

当前 471 行 Module 同时实现：

- Host 初始化等待和后台循环；
- 窗口预取、优先队列、版本淘汰；
- 同项目 backlog 和并发控制；
- 项目锁获取；
- Job claim、Agent 执行；
- 成功、重试、失败和日志回写。

前四项属于调度循环，后三项属于单次执行尝试。它们的依赖和测试场景不同，放在一起导致状态迁移缺少 Locality。

### Domain Event Module 过浅

`IJobDomainEvent`、`JobCreatedDomainEvent`、`IJobDomainEventDispatcher` 和 `JobDomainEventDispatcher` 只有一个事件、一个发布者和一个订阅者。删除测试表明，移除这些 Module 不会把复杂度分散到多个调用者，反而会消除通用事件分派脚手架。

### 目录按技术类型组织，运行概念分散

`Application/Services` 混合 CRUD、调度、执行和持久化端口；`Dtos/InMemoryScheduledTask.cs` 的文件名与类型名不一致；Minimal API 仍位于 `Controllers`。维护一个 Job 尝试需要跨多个技术型目录跳转。

## 设计

### 1. 保留外部入口，整理为 `Api`

当前未提交的 Minimal API 迁移是既定基线：

- 删除 MVC `JobsController`；
- 保留 `Program.cs` 中显式调用 `MapJobsApi()`；
- 将 `Controllers/EndpointExtension.cs` 重命名并移动为 `Api/JobsEndpointRouteBuilderExtensions.cs`；
- 扩展类命名为 `JobsEndpointRouteBuilderExtensions`，继续暴露 `MapJobsApi`；
- `Contracts/ScheduledTaskRequests.cs` 重命名为 `Contracts/JobRequests.cs`，类型名称和 JSON 契约不变。

路由仍为：

- `GET/POST /api/jobs`；
- `GET/PUT/DELETE /api/jobs/{id:guid}`；
- `GET /api/jobs/{id:guid}/logs`。

现有路由测试随 namespace 和文件位置更新，并补充至少一个真实 handler 响应测试，保护 Bens.Results 封装和用户名称传递。

### 2. 建立 `Scheduling` 运行切片

调度相关代码集中到 `Scheduling/`：

```text
Scheduling/
  Coordination/
    JobHostedService.cs
    JobSchedulerWakeSignal.cs
    IProjectExecutionLock.cs
  Attempts/
    JobAttemptRunner.cs
    JobAttemptResult.cs
  ScheduledJob.cs
  JobScheduleCalculator.cs
  JobSchedulingDefaults.cs
  IJobStore.cs
```

`Coordination` 集中预取、唤醒、内存队列和项目级串行派发，`Attempts` 集中一次执行的状态迁移、日志和重试。两个子 Module 共享的调度快照、时间计算、默认值和持久化 Seam 保留在 `Scheduling` 根部。

`JobHostedService` 只保留：

- 等待服务器初始化；
- 周期预取和唤醒等待；
- `PriorityQueue` 与版本淘汰；
- 按项目分发和 backlog；
- 获取项目锁；
- 调用单次执行 Module；
- 根据执行结果重新入队或移除内存项。

`ScheduledJob` 是内存调度快照，不属于外部 DTO。它保留当前 `InMemoryJob` 的字段和版本语义。

### 3. 提取深的单次执行 Module

新增 scoped 的具体类 `JobAttemptRunner`，不增加只有一个 Adapter 的 `IJobAttemptRunner` Seam。它提供一个入口，隐藏以下 Implementation：

1. 调用 `IJobStore.MarkRunningAsync` 认领 Job；
2. 把 `ScheduledJob` 转换为 `Job`；
3. 调用 Job Agent 执行 Adapter；
4. 计算下一次运行时间；
5. 成功时更新 Job 并写成功日志；
6. 失败时按现有规则更新重试或暂停状态，并写失败日志；
7. Job 已删除时返回丢弃结果；
8. 返回重新调度或丢弃的 `JobAttemptResult`。

项目锁仍由 `JobHostedService` 在创建 scoped `JobAttemptRunner` 之前获取，保持当前“先拿锁、后创建作用域和访问数据库”的顺序。

`JobAttemptResult` 只表达调度器需要知道的两种结果：

- `Reschedule`：携带更新后的 `ScheduledJob`；
- `Drop`：从 `_jobMap` 移除，不重新入队。

日志 attempt、`Guid.Empty` taskId、Job 不存在时的处理以及取消信号传递保持现状。

### 4. 用语义明确的唤醒 Module 取代 Domain Event

新增 singleton `JobSchedulerWakeSignal`：

- `JobAppService` 在 Job 创建并提交后调用它；
- 它只在当前规则要求时发出预取信号：Job 为 `Once`、已启用、状态为 `Pending`，且 `NextRunTime` 距当前时间不足 1 分钟；
- `JobHostedService` 的预取循环等待同一个信号；
- 更新和删除仍不主动唤醒调度器，保持现有行为。

删除 `Domain/Events`、`IJobDomainEventDispatcher` 和 `JobDomainEventDispatcher`。这个调整缩小 Interface，不引入通用事件总线。

### 5. 保留有价值的 Seam

以下 Seam 保留：

- `IJobStore`：隔离调度状态持久化，Adapter 仍为 `JobRepo`；
- `IProjectExecutionLock`：已有内存和 PostgreSQL 两类真实 Adapter；
- Agent 执行 Interface：隐藏项目 Task 生命周期以及 Agent/Agentflow 分支。

Agent 执行代码移动到 `Execution/`，命名调整为 `IJobAgentExecutor` 和 `JobAgentExecutor`，并把现有 primary constructor 改为显式构造函数，以符合仓库规则。执行语义不变。

`IJobTimeCalculator` 只有一个 Adapter，属于 hypothetical Seam。删除该 Interface，保留具体的 `JobScheduleCalculator` Module，并在 `JobAppService` 与 `JobAttemptRunner` 中直接注入。

本次不深化 `IJobStore` 的完成方法，也不把状态更新与日志写入合并为新事务；这会改变失败窗口，超出纯重组范围。

## 依赖方向

```text
Api -> Application -> Scheduling -> Execution
                         |
                         +-> IJobStore
                         +-> IProjectExecutionLock

Agw.Infrastructure -> IJobStore / IProjectExecutionLock
Execution -> Agw.Projects / Agw.Agents
```

`Agw.Data` 中的 anemic entities 保持不变。Infrastructure Adapter 继续依赖 `Agw.Jobs` 定义的 Seam，`Agw.Jobs` 不依赖 `Agw.Infrastructure`。

## 错误与取消语义

- 触发器错误继续通过 `AgwException` 与现有 `ErrorCodes` 表达；
- Agent 目标缺失、运行失败和项目 Task 更新失败的错误码不变；
- Job 不存在仍作为 stale in-memory entry 丢弃；
- 其他执行异常仍记录到 `LastError` 和 `JobLog`，并按原规则重试；
- 所有异步路径继续传递调用方 `CancellationToken`；
- 后台预取循环仍记录错误并继续运行。

## 测试策略

实施前先增加 characterization tests，再移动 Implementation：

1. `JobAttemptRunner`：周期成功后重新调度；
2. `JobAttemptRunner`：Once 成功后丢弃并暂停；
3. `JobAttemptRunner`：失败后按 30 秒重试；
4. `JobAttemptRunner`：重试耗尽后丢弃并禁用；
5. `JobAttemptRunner`：Job 不存在时丢弃且不写额外日志；
6. `JobSchedulerWakeSignal`：只唤醒符合当前条件的近期 Once Job；
7. Minimal API：路由集合不变，响应仍为 Bens.Results；
8. 现有 `JobAppService`、`JobRepo` 和项目锁测试全部继续通过。

完成后运行：

```bash
dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj
dotnet test Agw.slnx
```

## 非目标

- 不增加新触发器；
- 不修复或改变即时改期、即时取消行为；
- 不提供 exactly-once 保证；
- 不修改 Job/JobLog 数据库结构；
- 不增加调度参数配置；
- 不重构 Web Jobs 页面；
- 不清理与本次重组无关的代码或工作区改动。
