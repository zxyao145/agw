# Agw.Jobs Reorganization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reorganize `Agw.Jobs` around deep Api, Application, Scheduling, and Execution modules without changing routes, persistence, scheduling, retry, or Agent execution behavior.

**Architecture:** Keep Minimal API as the HTTP entry, concentrate in-memory scheduling in `Scheduling/Coordination/JobHostedService`, and move one-attempt state transitions into `Scheduling/Attempts/JobAttemptRunner`. Replace the generic one-event dispatcher with a semantic singleton wake signal, while retaining the real `IJobStore`, `IProjectExecutionLock`, and Job Agent execution seams.

**Tech Stack:** .NET 10, ASP.NET Core Minimal API, EF Core, xUnit v3, Cronos, Bens.Results.

## Global Constraints

- Preserve all unrelated staged and unstaged user changes.
- Do not create commits unless the user explicitly requests one; commit steps are intentionally omitted.
- Do not add or apply EF Core migrations.
- Keep all non-WebSocket JSON endpoints wrapped by `AgwApiResult` / Bens.Results.
- Do not introduce C# primary constructors.
- Use `DateTimeOffset` and `TimeProvider`; do not add `DateTime`.
- Preserve `/api/jobs` routes and generated JSON contracts.
- Preserve prefetch interval 1 minute, prefetch window 10 minutes, and retry delay 30 seconds.
- Preserve `MaxRetryCount` as retries after the initial attempt.
- Treat the current Minimal API migration as the baseline.

---

### Task 1: Move the Minimal API entry into the Api module

**Files:**
- Move: `src/server/Agw.Jobs/Controllers/EndpointExtension.cs` → `src/server/Agw.Jobs/Api/JobsEndpointRouteBuilderExtensions.cs`
- Move: `src/server/Agw.Jobs/Contracts/ScheduledTaskRequests.cs` → `src/server/Agw.Jobs/Contracts/JobRequests.cs`
- Modify: `src/server/Agw.Host/Program.cs`
- Modify: `tests/Agw.Jobs.Tests/EndpointExtensionTests.cs`
- Create: `tests/Agw.Jobs.Tests/JobsApiTests.cs`
- Modify: `tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj`

**Interfaces:**
- Produces: `Agw.Jobs.Api.JobsEndpointRouteBuilderExtensions.MapJobsApi(IEndpointRouteBuilder)`.
- Preserves: six existing `/api/jobs` route/method pairs and Bens.Results response types.

- [ ] **Step 1: Point the existing route test at the desired Api namespace and class name**

```csharp
using Agw.Jobs.Api;

Assert.Equal(
    [
        ("api/jobs", "GET"),
        ("api/jobs", "POST"),
        ("api/jobs/{id:guid}", "DELETE"),
        ("api/jobs/{id:guid}", "GET"),
        ("api/jobs/{id:guid}", "PUT"),
        ("api/jobs/{id:guid}/logs", "GET")
    ],
    routes);
```

Add `Microsoft.AspNetCore.TestHost` to the test project and add an HTTP characterization test using the desired `Agw.Jobs.Api` namespace. Its fixture must use an open in-memory SQLite connection and register `AgwDbContext`, `IRepository<>`, `JobRepo`, `IUnitOfWork`, `TimeProvider`, `JobScheduleCalculator`, `JobSchedulerWakeSignal`, and `JobAppService` before calling `MapJobsApi()`:

```csharp
[Fact]
public async Task ListJobs_ReturnsBensResultsEnvelope()
{
    await using var fixture = await JobsApiFixture.CreateAsync();

    var response = await fixture.Client.GetAsync(
        "/api/jobs",
        TestContext.Current.CancellationToken);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
        TestContext.Current.CancellationToken));
    Assert.Equal(0, body.RootElement.GetProperty("code").GetInt32());
    Assert.Equal("ok", body.RootElement.GetProperty("title").GetString());
    Assert.Equal(200, body.RootElement.GetProperty("statusCode").GetInt32());
    Assert.Empty(body.RootElement.GetProperty("data").EnumerateArray());
}
```

- [ ] **Step 2: Run the route test and verify RED**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --filter "FullyQualifiedName~EndpointExtensionTests|FullyQualifiedName~JobsApiTests"`

Expected: compile failure because `Agw.Jobs.Api` and `JobsEndpointRouteBuilderExtensions` do not exist.

- [ ] **Step 3: Move and rename the endpoint extension**

The resulting declaration must be:

```csharp
namespace Agw.Jobs.Api;

public static class JobsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapJobsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var routeGroup = endpoints.MapGroup("api").WithTags("jobs");
        // Preserve the six current route mappings and handlers verbatim.
        return endpoints;
    }
}
```

Update the Host using to `Agw.Jobs.Api`; do not change `app.MapJobsApi()` ordering. Rename only the contracts file; keep `JobCreateRequest`, `JobUpdateRequest`, and `JobLogResponse` unchanged.

- [ ] **Step 4: Run the route and HTTP tests and verify GREEN**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --filter "FullyQualifiedName~EndpointExtensionTests|FullyQualifiedName~JobsApiTests"`

Expected: route metadata is unchanged and `GET /api/jobs` returns a Bens.Results envelope with an empty data array.

---

### Task 2: Replace the generic created event with a scheduler wake signal

**Files:**
- Create: `src/server/Agw.Jobs/Scheduling/JobSchedulingDefaults.cs`
- Create: `src/server/Agw.Jobs/Scheduling/Coordination/JobSchedulerWakeSignal.cs`
- Create: `tests/Agw.Jobs.Tests/JobSchedulerWakeSignalTests.cs`
- Modify: `src/server/Agw.Jobs/Application/Services/JobAppService.cs`
- Modify: `src/server/Agw.Jobs/DependencyInjection.cs`
- Modify: `tests/Agw.Jobs.Tests/JobAppServiceTests.cs`
- Delete: `src/server/Agw.Jobs/Domain/Events/IJobDomainEvent.cs`
- Delete: `src/server/Agw.Jobs/Domain/Events/JobCreatedDomainEvent.cs`
- Delete: `src/server/Agw.Jobs/Application/Services/IJobDomainEventDispatcher.cs`
- Delete: `src/server/Agw.Jobs/Application/Services/JobDomainEventDispatcher.cs`

**Interfaces:**
- Produces: `JobSchedulerWakeSignal.NotifyCreated(Job)` and `JobSchedulerWakeSignal.WaitAsync(CancellationToken)`.
- Consumes: `TimeProvider` and `JobSchedulingDefaults.PrefetchInterval`.

- [ ] **Step 1: Write wake-signal tests against the desired interface**

```csharp
[Fact]
public async Task NotifyCreated_UpcomingEnabledOnceJob_ReleasesWaiter()
{
    var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
    var signal = new JobSchedulerWakeSignal(new TestTimeProvider(now));
    var wait = signal.WaitAsync(TestContext.Current.CancellationToken);

    signal.NotifyCreated(CreateJob(TriggerType.Once, now.AddSeconds(30), true, JobStatus.Pending));

    await wait.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
}

[Theory]
[InlineData(TriggerType.Interval, true, JobStatus.Pending)]
[InlineData(TriggerType.Once, false, JobStatus.Pending)]
[InlineData(TriggerType.Once, true, JobStatus.Paused)]
public async Task NotifyCreated_NonUrgentJob_DoesNotReleaseWaiter(
    TriggerType triggerType,
    bool isEnabled,
    JobStatus status)
{
    var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
    var signal = new JobSchedulerWakeSignal(new TestTimeProvider(now));
    using var cancellation = new CancellationTokenSource();
    var wait = signal.WaitAsync(cancellation.Token);

    signal.NotifyCreated(CreateJob(triggerType, now.AddSeconds(30), isEnabled, status));

    Assert.False(wait.IsCompleted);
    cancellation.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
}
```

- [ ] **Step 2: Run the wake-signal tests and verify RED**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --filter "FullyQualifiedName~JobSchedulerWakeSignalTests"`

Expected: compile failure because `JobSchedulerWakeSignal` does not exist.

- [ ] **Step 3: Implement the minimal wake signal**

```csharp
public static class JobSchedulingDefaults
{
    public static readonly TimeSpan PrefetchInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan PrefetchWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
}

public sealed class JobSchedulerWakeSignal
{
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    public JobSchedulerWakeSignal(TimeProvider timeProvider) => _timeProvider = timeProvider;

    public void NotifyCreated(Job job)
    {
        var now = _timeProvider.GetUtcNow();
        if (job.TriggerType == TriggerType.Once
            && job.IsEnabled
            && job.Status == JobStatus.Pending
            && job.NextRunTime < now.Add(JobSchedulingDefaults.PrefetchInterval))
        {
            _signal.Release();
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken) => _signal.WaitAsync(cancellationToken);
}
```

Inject the signal into `JobAppService`, call `NotifyCreated(entity)` after `SaveChangesAsync`, register it as singleton, and remove the event files and registrations.

- [ ] **Step 4: Run wake-signal and JobAppService tests**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --filter "FullyQualifiedName~JobSchedulerWakeSignalTests|FullyQualifiedName~JobAppServiceTests"`

Expected: all selected tests pass.

---

### Task 3: Organize the schedule calculator and Job Agent execution adapter

**Files:**
- Move: `src/server/Agw.Jobs/Application/Services/JobTimeCalculator.cs` → `src/server/Agw.Jobs/Scheduling/JobScheduleCalculator.cs`
- Delete: `src/server/Agw.Jobs/Application/Services/IJobTimeCalculator.cs`
- Move: `src/server/Agw.Jobs/Application/Services/AgentExecutor.cs` → `src/server/Agw.Jobs/Execution/JobAgentExecutor.cs`
- Move: `src/server/Agw.Jobs/Application/Services/IAgentExecutor.cs` → `src/server/Agw.Jobs/Execution/IJobAgentExecutor.cs`
- Create: `tests/Agw.Jobs.Tests/JobScheduleCalculatorTests.cs`
- Modify: `tests/Agw.Projects.Tests/AgentExecutorTests.cs`
- Modify: `src/server/Agw.Jobs/Application/Services/JobAppService.cs`
- Modify: `src/server/Agw.Jobs/DependencyInjection.cs`
- Modify: `tests/Agw.Jobs.Tests/JobAppServiceTests.cs`

**Interfaces:**
- Produces: `JobScheduleCalculator.GetNextRunTime(Job, DateTimeOffset)`.
- Produces: `IJobAgentExecutor.ExecuteAsync(Job, CancellationToken)` implemented by `JobAgentExecutor`.

- [ ] **Step 1: Write schedule calculator tests against the desired concrete Module**

Cover exact current behavior:

```csharp
[Theory]
[InlineData(TriggerType.Once, "2026-07-15T09:00:00Z", "2026-07-15T09:00:00+00:00")]
[InlineData(TriggerType.Interval, "00:15:00", "2026-07-15T08:15:00+00:00")]
public void GetNextRunTime_ValidTrigger_ReturnsExpected(
    TriggerType triggerType,
    string triggerValue,
    string expected)
{
    var now = DateTimeOffset.Parse("2026-07-15T08:00:00Z");
    var calculator = new JobScheduleCalculator();
    var job = new Job { TriggerType = triggerType, TriggerValue = triggerValue };

    Assert.Equal(DateTimeOffset.Parse(expected), calculator.GetNextRunTime(job, now));
}
```

- [ ] **Step 2: Run calculator tests and verify RED**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --filter "FullyQualifiedName~JobScheduleCalculatorTests"`

Expected: compile failure because `JobScheduleCalculator` does not exist.

- [ ] **Step 3: Move and rename the concrete modules**

Move the calculator implementation unchanged and delete its one-adapter Interface. Rename `AgentExecutor`/`IAgentExecutor` to `JobAgentExecutor`/`IJobAgentExecutor`, preserve `ExecuteAsync`, and replace the primary constructor with explicit fields and constructor injection.

- [ ] **Step 4: Update references and run focused tests**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --filter "FullyQualifiedName~JobScheduleCalculatorTests|FullyQualifiedName~JobAppServiceTests"`

Run: `dotnet test tests/Agw.Projects.Tests/Agw.Projects.Tests.csproj --filter "FullyQualifiedName~AgentExecutorTests"`

Expected: all selected tests pass.

---

### Task 4: Extract the one-attempt state machine

**Files:**
- Create: `src/server/Agw.Jobs/Scheduling/ScheduledJob.cs`
- Create: `src/server/Agw.Jobs/Scheduling/Attempts/JobAttemptResult.cs`
- Create: `src/server/Agw.Jobs/Scheduling/Attempts/JobAttemptRunner.cs`
- Move: `src/server/Agw.Jobs/Application/Services/IJobStore.cs` → `src/server/Agw.Jobs/Scheduling/IJobStore.cs`
- Create: `tests/Agw.Jobs.Tests/JobAttemptRunnerTests.cs`

**Interfaces:**
- Consumes: `IJobStore`, `IJobAgentExecutor`, `JobScheduleCalculator`, `TimeProvider`, `ILogger<JobAttemptRunner>`.
- Produces: `Task<JobAttemptResult> RunAsync(ScheduledJob, CancellationToken)`.
- Produces result cases: `JobAttemptResult.Reschedule(ScheduledJob Job)` and `JobAttemptResult.Drop`.

- [ ] **Step 1: Write the successful recurring-attempt test**

Use in-test fakes for `IJobStore` and `IJobAgentExecutor`. The fake store records calls and the fake executor returns a fixed Task ID.

```csharp
[Fact]
public async Task RunAsync_RecurringJobSucceeds_ReturnsRescheduleAndWritesSuccessLog()
{
    var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
    var store = new RecordingJobStore { MarkRunningResult = true };
    var taskId = Guid.NewGuid();
    var runner = CreateRunner(store, new StubJobAgentExecutor(taskId), now);
    var scheduled = CreateScheduledJob(TriggerType.Interval, "00:15:00", retryCount: 0, maxRetryCount: 3);

    var result = await runner.RunAsync(scheduled, TestContext.Current.CancellationToken);

    var reschedule = Assert.IsType<JobAttemptResult.Reschedule>(result);
    Assert.Equal(now.AddMinutes(15), reschedule.Job.NextRunTime);
    Assert.Equal(0, reschedule.Job.RetryCount);
    Assert.Equal(taskId, store.LastLog?.TaskId);
    Assert.True(store.LastLog?.Success);
    Assert.Equal(1, store.LastLog?.Attempt);
}
```

- [ ] **Step 2: Run the test and verify RED**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --filter "FullyQualifiedName~JobAttemptRunnerTests.RunAsync_RecurringJobSucceeds"`

Expected: compile failure because the new Scheduling types do not exist.

- [ ] **Step 3: Implement the minimum successful path**

Implement `ScheduledJob` with the current in-memory fields and version, the two result records, and a scoped `JobAttemptRunner`. On success: mark running, execute Agent, calculate next time, mark succeeded, add the success log, and return `Reschedule` or `Drop`.

- [ ] **Step 4: Verify the successful path GREEN**

Run the same filtered test. Expected: PASS.

- [ ] **Step 5: Add failing characterization tests for false claim, retry, exhaustion, and missing Job**

Required assertions:

- `MarkRunningAsync == false` returns `Drop` without Agent execution or log;
- first failure with `MaxRetryCount = 3` calls `MarkRetryAsync`, logs attempt 1, and reschedules at `now + 30 seconds`;
- successful `Once` execution calls `MarkSucceededAsync` without a next time and returns `Drop`;
- failure with `RetryCount = MaxRetryCount` calls `MarkFailedAsync`, logs final attempt, and returns `Drop`;
- `AgwException(ErrorCodes.JobNotFound)` returns `Drop` without additional bookkeeping;
- an Agent failure before a Task ID is returned logs `Guid.Empty`.

- [ ] **Step 6: Run the new tests and verify RED for the missing branches**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --filter "FullyQualifiedName~JobAttemptRunnerTests"`

Expected: new branch tests fail with missing calls or wrong result types.

- [ ] **Step 7: Move the existing failure and retry logic into `JobAttemptRunner`**

Copy the existing ordering and error classification exactly from `JobHostedService.ExecuteOneAsync`; do not combine store methods or change transaction scope. Use `JobSchedulingDefaults.RetryDelay`.

- [ ] **Step 8: Run all JobAttemptRunner tests and verify GREEN**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --filter "FullyQualifiedName~JobAttemptRunnerTests"`

Expected: all JobAttemptRunner tests pass.

---

### Task 5: Reduce JobHostedService to scheduling responsibilities

**Files:**
- Move: `src/server/Agw.Jobs/HostedService/JobHostedService.cs` → `src/server/Agw.Jobs/Scheduling/Coordination/JobHostedService.cs`
- Move: `src/server/Agw.Jobs/External/IProjectExecutionLock.cs` → `src/server/Agw.Jobs/Scheduling/Coordination/IProjectExecutionLock.cs`
- Delete: `src/server/Agw.Jobs/Dtos/InMemoryScheduledTask.cs`
- Modify: `src/server/Agw.Jobs/DependencyInjection.cs`
- Modify: `src/server/Agw.Infrastructure/DependencyInjection.cs`
- Modify: `src/server/Agw.Infrastructure/Repositories/JobRepo.cs`
- Modify: `src/server/Agw.Infrastructure/Jobs/DistributedProjectExecutionLock.cs`
- Modify: `src/server/Agw.Infrastructure/Jobs/InMemoryProjectExecutionLock.cs`
- Modify: `src/server/Agw.Infrastructure/Jobs/ProjectExecutionLockRouter.cs`

**Interfaces:**
- Consumes: `JobSchedulerWakeSignal`, `JobAttemptRunner`, `IProjectExecutionLock`, `IJobStore`.
- Preserves: queue versioning, wake behavior, project backlog, lock ordering, and fire-and-forget execution tracking.

- [ ] **Step 1: Point production references at `Agw.Jobs.Scheduling` and verify RED**

Update usings and DI registrations to the desired namespace before moving `JobHostedService` and `IProjectExecutionLock`.

Run: `dotnet build src/server/Agw.Jobs/Agw.Jobs.csproj`

Expected: compile failure until the files move and `ExecuteOneAsync` is replaced.

- [ ] **Step 2: Move scheduling files and replace `InMemoryJob` with `ScheduledJob`**

Preserve `Upsert`, `TryPeekLatest`, `TryDequeueLatest`, version comparison, `_runningProjects`, `_projectBacklog`, and `_runningExecutions`. Replace local timing fields with `JobSchedulingDefaults`.

- [ ] **Step 3: Replace `ExecuteOneAsync` with the scoped deep Module call**

The scheduling code must retain lock-before-scope ordering:

```csharp
await using var projectLock = await _projectExecutionLock.AcquireAsync(job.ProjectId, cancellationToken);
using var scope = _scopeFactory.CreateScope();
var runner = scope.ServiceProvider.GetRequiredService<JobAttemptRunner>();
var result = await runner.RunAsync(job, cancellationToken);

if (result is JobAttemptResult.Reschedule reschedule)
{
    UpsertScheduledJob(reschedule.Job);
}
else
{
    _jobMap.TryRemove(job.JobId, out _);
}
```

- [ ] **Step 4: Register the final module graph**

`AddJobs` must register:

```csharp
services.AddHostedService<JobHostedService>();
services.AddScoped<IJobAgentExecutor, JobAgentExecutor>();
services.AddScoped<JobAttemptRunner>();
services.AddSingleton<JobScheduleCalculator>();
services.AddSingleton<JobSchedulerWakeSignal>();
services.AddScoped<JobAppService>();
```

Keep Infrastructure Adapter lifetimes unchanged.

- [ ] **Step 5: Run the Jobs tests**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj`

Expected: all tests pass, including the staged Minimal API route test.

---

### Task 6: Synchronize documentation and verify the repository

**Files:**
- Modify: `src/server/Agw.Jobs/README.zh-CN.md`
- Modify: `docs/superpowers/specs/2026-07-15-agw-jobs-reorganization-design.md` only if implementation names differ for a documented reason
- Inspect only: all unrelated working-tree files

**Interfaces:**
- Documents the final Api/Application/Scheduling/Execution layout and unchanged runtime semantics.

- [ ] **Step 1: Update README paths and architecture names**

Replace references to `Controllers`, `HostedService`, `Dtos`, `AgentExecutor`, `JobTimeCalculator`, and Domain Events with their final names. Explain `JobAttemptRunner` and `JobSchedulerWakeSignal`; keep all usage examples and external routes unchanged.

- [ ] **Step 2: Run focused verification**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj`

Run: `dotnet test tests/Agw.Projects.Tests/Agw.Projects.Tests.csproj --filter "FullyQualifiedName~AgentExecutorTests"`

Expected: zero failed tests.

- [ ] **Step 3: Run repository verification**

Run: `dotnet test Agw.slnx`

Expected: zero failed tests. Existing NuGet source-mapping or known-vulnerability warnings may remain, but no new compiler errors or test failures are allowed.

- [ ] **Step 4: Check the final diff**

Run: `git diff --check`

Run: `git status --short`

Confirm every changed line belongs to the approved reorganization or the pre-existing user changes. Do not stage or commit.
