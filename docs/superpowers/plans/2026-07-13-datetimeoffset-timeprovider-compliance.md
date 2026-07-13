# DateTimeOffset and TimeProvider Compliance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove `DateTime` from non-generated server and test code, migrate persisted/API timestamps to `DateTimeOffset`, and route production clock and delay behavior through `TimeProvider`.

**Architecture:** Register `TimeProvider.System` at the host composition root and inject `TimeProvider` into DI-managed services whose behavior depends on time. Use `TimeProvider.System` only in static or leaf adapters where constructor propagation would not create a useful test seam. Preserve UTC semantics and capture one timestamp per logical operation.

**Tech Stack:** .NET 10, ASP.NET Core dependency injection, EF Core, xUnit v3, Next.js OpenAPI TypeScript generation.

## Global Constraints

- Work only in `/Users/ben/source/repos/agw/.worktrees/refactor-changeto-datetimeoffset` on branch `refactor/changeto-datetimeoffset`.
- Do not use `DateTime` in backend code; use `DateTimeOffset` instead.
- Use `TimeProvider` whenever it is applicable.
- Do not add a custom production clock abstraction.
- Use explicit constructors and backing fields; do not add C# primary constructors.
- Do not edit historical EF migrations or the EF model snapshot, and do not create or apply a migration automatically.
- Regenerate tracked OpenAPI artifacts from the running backend; do not hand-edit generated declarations.
- Do not create commits unless the user separately authorizes them.
- Preserve unrelated code and formatting.
- Baseline: `dotnet test Agw.slnx --no-restore` has exactly four unrelated failures in `Agw.Files.Tests.PathSecurityServiceTests`:
  - `TryResolvePath_WhenPathIsAbsoluteSibling_RejectsPath`
  - `TryResolvePath_WhenPathTraversesToSibling_RejectsPath`
  - `TryResolvePath_WhenSiblingSharesRootPrefix_RejectsPath`
  - `TryResolvePath_WhenPathSharesAdditionalRootPrefix_RejectsPath`
- The implementation must introduce no additional test failure.

---

### Task 1: Add a deterministic test TimeProvider

**Files:**
- Create: `tests/TestTimeProvider.cs`
- Modify: `tests/Directory.Build.props`

**Interfaces:**
- Produces: `Agw.Testing.TestTimeProvider(DateTimeOffset utcNow)` with `GetUtcNow()` and `SetUtcNow(DateTimeOffset)`.
- Consumed by: focused tests in Tasks 3–7.

- [ ] **Step 1: Add the shared test provider**

Create `tests/TestTimeProvider.cs`:

~~~csharp
namespace Agw.Testing;

public sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public TestTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void SetUtcNow(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }
}
~~~

- [ ] **Step 2: Link the helper into every test project**

Add to `tests/Directory.Build.props`:

~~~xml
<ItemGroup>
  <Compile Include="$(MSBuildThisFileDirectory)TestTimeProvider.cs" Link="TestTimeProvider.cs" />
</ItemGroup>
~~~

- [ ] **Step 3: Verify all test projects compile the helper**

Run:

~~~bash
dotnet build Agw.slnx --no-restore
~~~

Expected: build succeeds. Existing NU1507/IDE0005/CS1574 warnings may remain; no compiler error references `TestTimeProvider`.

---

### Task 2: Migrate shared persistence and contract types

**Files:**
- Create: `tests/Agw.Shared.Tests/TemporalTypePolicyTests.cs`
- Modify: `src/server/Agw.Shared/Data/BaseEntity.cs`
- Modify: `src/server/Agw.Shared/Data/Entities/Tasks/TaskRecord.cs`
- Modify: `src/server/Agw.Shared/Data/Entities/Agents/AgentflowTrace.cs`
- Modify: `src/server/Agw.Shared/Contracts/Tasks/FileRequests.cs`
- Modify: `src/server/Agw.Shared/Contracts/Tasks/TaskRequests.cs`
- Modify: `src/server/Agw.Shared/Contracts/Tasks/TaskProjection.cs`
- Modify: `src/server/Agw.Shared/Contracts/Agents/AgentflowTraceDto.cs`

**Interfaces:**
- Produces: `DateTimeOffset CreateTime`, `DateTimeOffset? UpdateTime`, `DateTimeOffset? FinishedTime`, `DateTimeOffset StartTimeUtc`, and matching contract properties.
- Consumed by: all downstream server modules.

- [ ] **Step 1: Add failing reflection coverage**

Create `tests/Agw.Shared.Tests/TemporalTypePolicyTests.cs`:

~~~csharp
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Tasks;

namespace Agw.Shared.Tests;

public class TemporalTypePolicyTests
{
    [Fact]
    public void PersistedAndContractTimestamps_UseDateTimeOffset()
    {
        AssertPropertyType<BaseEntity>(nameof(BaseEntity.CreateTime), typeof(DateTimeOffset));
        AssertPropertyType<BaseEntity>(nameof(BaseEntity.UpdateTime), typeof(DateTimeOffset?));
        AssertPropertyType<TaskRecord>(nameof(TaskRecord.CreateTime), typeof(DateTimeOffset));
        AssertPropertyType<TaskRecord>(nameof(TaskRecord.UpdateTime), typeof(DateTimeOffset?));
        AssertPropertyType<TaskRecord>(nameof(TaskRecord.FinishedTime), typeof(DateTimeOffset?));
        AssertPropertyType<AgentflowTrace>(nameof(AgentflowTrace.StartTimeUtc), typeof(DateTimeOffset));
        AssertPropertyType<TaskProjection>(nameof(TaskProjection.CreateTime), typeof(DateTimeOffset));
        AssertPropertyType<TaskProjection>(nameof(TaskProjection.UpdateTime), typeof(DateTimeOffset?));
        AssertPropertyType<TaskProjection>(nameof(TaskProjection.FinishedTime), typeof(DateTimeOffset?));
        AssertPropertyType<AgentflowTraceDto>(nameof(AgentflowTraceDto.StartTimeUtc), typeof(DateTimeOffset));
    }

    private static void AssertPropertyType<T>(string propertyName, Type expectedType)
    {
        var property = typeof(T).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expectedType, property.PropertyType);
    }
}
~~~

- [ ] **Step 2: Prove the policy test is red**

Run:

~~~bash
dotnet test tests/Agw.Shared.Tests/Agw.Shared.Tests.csproj --no-restore --filter FullyQualifiedName~TemporalTypePolicyTests
~~~

Expected: FAIL because current properties use `DateTime`/`DateTime?`.

- [ ] **Step 3: Change shared entity and contract declarations**

Apply these exact shapes:

~~~csharp
// BaseEntity
public DateTimeOffset CreateTime { get; set; }
public DateTimeOffset? UpdateTime { get; set; }

// TaskRecord
public DateTimeOffset? FinishedTime { get; set; }
public DateTimeOffset CreateTime { get; set; }
public DateTimeOffset? UpdateTime { get; set; }

// AgentflowTrace
public DateTimeOffset StartTimeUtc { get; set; }

// FileRequests
public DateTimeOffset? ModifiedTime { get; set; }

// TaskProjection
public DateTimeOffset CreateTime { get; init; }
public DateTimeOffset? UpdateTime { get; init; }
public DateTimeOffset? FinishedTime { get; init; }

// AgentflowTraceDto
public DateTimeOffset StartTimeUtc { get; init; }
~~~

In `TaskRequests.cs`, replace both positional `DateTime CreateTime` fields with `DateTimeOffset CreateTime` and both `DateTime? UpdateTime` fields with `DateTimeOffset? UpdateTime`.

- [ ] **Step 4: Prove the shared policy is green**

Run:

~~~bash
dotnet test tests/Agw.Shared.Tests/Agw.Shared.Tests.csproj --no-restore
~~~

Expected: all `Agw.Shared.Tests` tests pass.

---

### Task 3: Register TimeProvider and migrate Provider and Skill clocks

**Files:**
- Modify: `src/server/Agw.Host/Program.cs`
- Modify: `src/server/Agw.Providers/Domain/Services/ModelDomainService.cs`
- Modify: `src/server/Agw.Providers/Domain/Services/ModelProviderDomainService.cs`
- Modify: `src/server/Agw.Providers/Domain/Services/ProviderDomainService.cs`
- Modify: `src/server/Agw.Skills/Domain/Services/SkillDomainService.cs`
- Modify: `src/server/Agw.Skills/Contracts/Manager/SkillRequests.cs`
- Modify: `tests/Agw.Tasks.Tests/ProviderAppServiceTests.cs`
- Modify: `tests/Agw.Skills.Tests/SkillDomainServiceTests.cs`

**Interfaces:**
- Produces: singleton `TimeProvider.System` registration.
- Produces: explicit `...(TimeProvider timeProvider)` constructors for the four domain services.
- Consumes: `TestTimeProvider` from Task 1 and metadata types from Task 2.

- [ ] **Step 1: Make focused metadata tests deterministic**

In `SkillDomainServiceTests` and provider test setup, use:

~~~csharp
using Agw.Testing;

private static readonly DateTimeOffset UtcNow =
    new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);

private readonly TestTimeProvider _timeProvider = new(UtcNow);
private readonly SkillDomainService _service;

public SkillDomainServiceTests()
{
    _service = new SkillDomainService(_timeProvider);
}
~~~

Replace range assertions with exact assertions such as:

~~~csharp
Assert.Equal(UtcNow, skill.CreateTime);
Assert.Equal(UtcNow, skill.UpdateTime);
~~~

- [ ] **Step 2: Prove changed constructor expectations are red**

Run:

~~~bash
dotnet test tests/Agw.Skills.Tests/Agw.Skills.Tests.csproj --no-restore
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --no-restore --filter FullyQualifiedName~ProviderAppServiceTests
~~~

Expected: FAIL until production services accept and use `TimeProvider`.

- [ ] **Step 3: Register the system provider**

Immediately after `builder.Services.AddSingleton(dataPaths);` in `Program.cs`, add:

~~~csharp
builder.Services.AddSingleton(TimeProvider.System);
~~~

Change the nearby non-nullable value-type comment example from `DateTime` to `DateTimeOffset`.

- [ ] **Step 4: Inject and use TimeProvider in the four domain services**

Add this explicit pattern to each class:

~~~csharp
private readonly TimeProvider _timeProvider;

public SkillDomainService(TimeProvider timeProvider)
{
    _timeProvider = timeProvider;
}
~~~

Use `_timeProvider.GetUtcNow()` instead of direct framework clock access. Capture one `now` value per create/update operation. Change `ProviderDomainService.NormalizeAuthConfigs(..., DateTime now)` to `DateTimeOffset now`.

In `SkillRequests.cs`, change `CreateTime`/`UpdateTime` to `DateTimeOffset`/`DateTimeOffset?`.

- [ ] **Step 5: Run Provider and Skill tests**

Run:

~~~bash
dotnet test tests/Agw.Skills.Tests/Agw.Skills.Tests.csproj --no-restore
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --no-restore --filter FullyQualifiedName~ProviderAppServiceTests
~~~

Expected: both commands pass.

---

### Task 4: Migrate Agents and A2A timestamps and clocks

**Files:**
- Modify: `src/server/Agw.Agents/Definitions/Contracts/AgentRequests.cs`
- Modify: `src/server/Agw.Agents/Definitions/Contracts/AgentflowTraceRequests.cs`
- Modify: `src/server/Agw.Agents/Definitions/Controllers/TracesController.cs`
- Modify: `src/server/Agw.Agents/Definitions/Domain/AgentDomainService.cs`
- Modify: `src/server/Agw.Agents/Definitions/Domain/AgentflowDomainService.cs`
- Modify: `src/server/Agw.Agents/Definitions/Domain/McpToolServerDomainService.cs`
- Modify: `src/server/Agw.Agents/Definitions/Agents/AgentflowAppService.cs`
- Modify: `src/server/Agw.A2A/TaskStore.cs`
- Modify: `src/server/Agw.A2A/A2AAgentExecutionBridge.cs`
- Modify: `tests/Agw.Agents.Tests/AgentDomainServiceTests.cs`
- Modify: `tests/Agw.Agents.Tests/AgentflowDomainServiceTests.cs`
- Modify: `tests/Agw.Agents.Tests/McpToolServerDomainServiceTests.cs`
- Modify: `tests/Agw.Agents.Tests/AgentflowTraceAppServiceTests.cs`
- Modify: `tests/Agw.Agents.Tests/AgentflowTraceTests.cs`
- Modify: `tests/Agw.A2A.Tests/TaskStoreTests.cs`

**Interfaces:**
- Produces: `DateTimeOffset? FromUtc/ToUtc` and `DateTimeOffset` metadata in Agents API contracts.
- Produces: explicit `TimeProvider` constructor dependencies in Agents domain/app services and A2A bridge/store.
- Consumes: host registration from Task 3 and shared types from Task 2.

- [ ] **Step 1: Update focused Agents tests to fixed timestamps**

Use this pattern in each domain service test:

~~~csharp
using Agw.Testing;

private static readonly DateTimeOffset UtcNow =
    new(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);

private readonly AgentDomainService _service =
    new(new TestTimeProvider(UtcNow));
~~~

Replace range assertions with exact metadata equality. In `TaskStoreTests`, cover a saved `AgentTask` whose status timestamp is absent; construct `TaskStore` with `TestTimeProvider` and assert persisted context/record timestamps equal `UtcNow`.

- [ ] **Step 2: Prove focused tests are red**

Run:

~~~bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentDomainServiceTests|FullyQualifiedName~AgentflowDomainServiceTests|FullyQualifiedName~McpToolServerDomainServiceTests"
dotnet test tests/Agw.A2A.Tests/Agw.A2A.Tests.csproj --no-restore --filter FullyQualifiedName~TaskStoreTests
~~~

Expected: FAIL on missing `TimeProvider` constructors and/or non-deterministic timestamps.

- [ ] **Step 3: Convert Agents contracts and query parameters**

Apply:

~~~csharp
// Agent response fields
DateTimeOffset CreateTime,
DateTimeOffset? UpdateTime,

// Trace request/controller parameters
DateTimeOffset? FromUtc,
DateTimeOffset? ToUtc
~~~

- [ ] **Step 4: Inject TimeProvider into Agents services**

Add explicit constructors and `private readonly TimeProvider _timeProvider;` to the three domain services and `AgentflowAppService`. Use `_timeProvider.GetUtcNow()`.

In `AgentflowAppService.UpdateAsync`, capture one value before rebuilding graph items:

~~~csharp
var now = _timeProvider.GetUtcNow();

node.CreateTime = existing.CreateTime == default ? now : existing.CreateTime;
node.UpdateTime = now;
edge.CreateTime = existing.CreateTime == default ? now : existing.CreateTime;
edge.UpdateTime = now;
~~~

- [ ] **Step 5: Inject TimeProvider into A2A services**

Change `TaskStore` to accept and store `TimeProvider`. In `SaveTaskAsync`:

~~~csharp
var now = _timeProvider.GetUtcNow();
var statusTimestampUtc = task.Status?.Timestamp ?? now;
~~~

Convert `A2AAgentExecutionBridge` from its primary constructor to an explicit constructor with `IServiceScopeFactory` and `TimeProvider` fields. Set `TaskProjection.CreateTime` with `_timeProvider.GetUtcNow()`.

- [ ] **Step 6: Convert remaining Agents/A2A fixtures**

Use `new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero)` for fixed trace values. Preserve JSON names and ordering semantics.

- [ ] **Step 7: Run Agents and A2A tests**

Run:

~~~bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore
dotnet test tests/Agw.A2A.Tests/Agw.A2A.Tests.csproj --no-restore
~~~

Expected: both test projects pass.

---

### Task 5: Migrate Tasks and infrastructure persistence flows

**Files:**
- Modify: `src/server/Agw.Tasks/Application/TaskExecutionSnapshots.cs`
- Modify: `src/server/Agw.Tasks/Application/TaskExecutionMapper.cs`
- Modify: `src/server/Agw.Tasks/Application/TaskExecutionAppService.cs`
- Modify: `src/server/Agw.Tasks/Application/ProjectContextAppService.cs`
- Modify: `src/server/Agw.Tasks/Application/TaskSessionBindingService.cs`
- Modify: `src/server/Agw.Tasks/Domain/Services/EfCoreChatHistoryProvider.cs`
- Modify: `src/server/Agw.Tasks/Domain/Services/ProjectDomainService.cs`
- Modify: `src/server/Agw.Infrastructure/Data/DbSeeder.cs`
- Modify: `tests/Agw.Tasks.Tests/TaskRecordDomainServiceTests.cs`
- Modify: `tests/Agw.Tasks.Tests/TaskAppServiceTests.cs`
- Modify: `tests/Agw.Tasks.Tests/TaskExecutionAppServiceTests.cs`
- Modify: `tests/Agw.Tasks.Tests/TaskSessionBindingServiceTests.cs`
- Modify: `tests/Agw.Tasks.Tests/ProjectContextAppServiceTests.cs`
- Modify: `tests/Agw.Tasks.Tests/ProjectContextsControllerTests.cs`
- Modify: `tests/Agw.Tasks.Tests/ProjectDomainServiceTests.cs`
- Modify: `tests/Agw.Tasks.Tests/EfCoreChatHistoryProviderTests.cs`
- Modify: `tests/Agw.Tasks.Tests/Infrastructure/AgwDbContextIntegrationTests.cs`
- Modify: `tests/Agw.Tasks.Tests/ProjectContextUsageRecorderTests.cs`

**Interfaces:**
- Produces: task snapshot, summary, projection, mapper, and persistence timestamps as `DateTimeOffset`.
- Produces: explicit `TimeProvider` dependencies for listed services that read the clock.
- Consumes: shared entity and contract types from Task 2.

- [ ] **Step 1: Make Project domain tests deterministic**

Construct `ProjectDomainService` with `new TestTimeProvider(UtcNow)` and assert:

~~~csharp
Assert.Equal(UtcNow, project.CreateTime);
Assert.Equal(UtcNow, project.UpdateTime);
~~~

Run:

~~~bash
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --no-restore --filter FullyQualifiedName~ProjectDomainServiceTests
~~~

Expected: FAIL until the service accepts and uses `TimeProvider`.

- [ ] **Step 2: Convert task application types end to end**

In `TaskExecutionSnapshots.cs`, change every create/update/start/finish positional timestamp from `DateTime` to `DateTimeOffset` while preserving nullability.

In `TaskExecutionMapper.cs` use:

~~~csharp
private static DateTimeOffset? GetStartedTime(TaskProjection task) =>
~~~

Keep existing ordering and fallback expressions unchanged.

- [ ] **Step 3: Inject TimeProvider into task services**

Add explicit `TimeProvider` constructor dependencies and fields to `TaskExecutionAppService`, `ProjectContextAppService`, `TaskSessionBindingService`, `EfCoreChatHistoryProvider`, and `ProjectDomainService`.

Replace clock reads with `_timeProvider.GetUtcNow()`. Change helper parameters such as `GetOrCreateContextAsync(..., DateTime now)` to `DateTimeOffset now`. Capture one timestamp per logical operation and pass it through helpers.

- [ ] **Step 4: Inject TimeProvider into DbSeeder**

Add `TimeProvider timeProvider` to the existing explicit constructor and store it in `_timeProvider`. Replace every direct clock read, including commented examples. Capture one timestamp per seed batch and reuse it across related entities.

- [ ] **Step 5: Convert Task fixtures and helper signatures**

Apply these exact conversions:

~~~csharp
// Fixture current time without a deterministic assertion
TimeProvider.System.GetUtcNow()

// Fixed UTC instant
new DateTimeOffset(2024, 1, 1, 3, 0, 0, TimeSpan.Zero)

// Helper signature
DateTimeOffset createTime
~~~

When a service constructor changes, pass a shared `TestTimeProvider`.

- [ ] **Step 6: Run Tasks tests**

Run:

~~~bash
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --no-restore
~~~

Expected: all `Agw.Tasks.Tests` tests pass.

---

### Task 6: Migrate integration, setup, host, and file boundary clocks

**Files:**
- Modify: `src/server/Agw.Integrations/Contracts/Manager/AppInstanceListItemResponse.cs`
- Modify: `src/server/Agw.Integrations/Controllers/IntegrationsController.cs`
- Modify: `src/server/Agw.Integrations/Controllers/OauthController.cs`
- Modify: `src/server/Agw.Setup/Services/JsonInitializationStateStore.cs`
- Modify: `src/server/Agw.Setup/Controllers/SetupController.cs`
- Modify: `src/server/Agw.Host/Controllers/AuthController.cs`
- Modify: `src/server/Agw.Files/Application/Storage/Resolver/ProjectScopedFileSystemResolver.cs`
- Modify: `src/server/Agw.Files/Application/Storage/Sftp/SftpFileSystem.cs`
- Modify: `tests/Agw.Tasks.Tests/Integrations/IntegrationsControllerTests.cs`
- Modify: `tests/Agw.Tasks.Tests/Integrations/OauthControllerTests.cs`
- Modify: `tests/Agw.Setup.Tests/RequestTrustAndSetupCodeTests.cs`
- Modify: `tests/Agw.Host.Tests/DashboardControllerTests.cs`
- Modify: `tests/Agw.Host.Tests/ProjectTraceCleanupTests.cs`

**Interfaces:**
- Produces: `DateTimeOffset` integration response metadata and OAuth expiry calculations.
- Produces: injectable clocks for auth throttling, setup state, integration expiry, and resolver cache timestamps.
- Consumes: host `TimeProvider` registration from Task 3.

- [ ] **Step 1: Make OAuth expiry tests deterministic**

Construct `OAuthController` with `new TestTimeProvider(UtcNow)`. For `expires_in`, assert:

~~~csharp
Assert.Equal(UtcNow.AddSeconds(expiresIn), token.ExpiresAtUtc);
~~~

Run:

~~~bash
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --no-restore --filter FullyQualifiedName~OauthControllerTests
~~~

Expected: FAIL until `OAuthController` accepts and uses `TimeProvider`.

- [ ] **Step 2: Convert integration metadata and clocks**

Change `AppInstanceListItemResponse.CreateTime` to `DateTimeOffset` and `UpdateTime` to `DateTimeOffset?`.

Inject `TimeProvider` into both integration controllers. Use `_timeProvider.GetUtcNow()` for list/create/authorization timestamps and assign `AppInstance.CreateTime = now` directly.

Change the OAuth helper to:

~~~csharp
private static DateTimeOffset? ResolveExpiresAtUtc(
    JsonElement tokenResponse,
    DateTimeOffset nowUtc)
~~~

For `expires_in`, return `nowUtc.AddSeconds(expiresIn)`.

- [ ] **Step 3: Inject clocks into setup and host auth boundaries**

Add explicit `TimeProvider` dependencies to `JsonInitializationStateStore`, `SetupController`, and `AuthController`. Replace direct clock calls with `_timeProvider.GetUtcNow()` and pass that value to `AuthenticationAttemptLimiter`.

- [ ] **Step 4: Update file timestamps surgically**

Inject `TimeProvider` into `ProjectScopedFileSystemResolver` and use it for cache entries.

In leaf-created `SftpFileSystem`, use:

~~~csharp
TimeProvider.System.GetUtcNow()
~~~

Do not alter `SftpFileSystemFactory` solely to pass a provider.

- [ ] **Step 5: Convert boundary fixtures and constructors**

Use explicit `DateTimeOffset` literals. Pass `TestTimeProvider` whenever a changed controller/service is manually constructed.

- [ ] **Step 6: Run boundary tests**

Run:

~~~bash
dotnet test tests/Agw.Setup.Tests/Agw.Setup.Tests.csproj --no-restore
dotnet test tests/Agw.Host.Tests/Agw.Host.Tests.csproj --no-restore
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --no-restore --filter "FullyQualifiedName~IntegrationsControllerTests|FullyQualifiedName~OauthControllerTests"
~~~

Expected: all selected tests pass. Do not use `Agw.Files.Tests` as a clean gate because its four path-security failures are part of the recorded baseline.

---

### Task 7: Make Jobs and Redis scheduling TimeProvider-aware

**Files:**
- Modify: `src/server/Agw.Jobs/Application/Services/JobAppService.cs`
- Modify: `src/server/Agw.Jobs/HostedService/JobHostedService.cs`
- Modify: `src/server/Agw.Infrastructure/Repositories/JobRepo.cs`
- Modify: `src/server/Agw.Infrastructure/Jobs/RedisProjectExecutionLock.cs`
- Modify: `src/server/Agw.Shared/Redis/RedisLock.cs`
- Modify: `tests/Agw.Jobs.Tests/JobStoreTests.cs`
- Modify: `tests/Agw.Tasks.Tests/JobRowVersionTests.cs`

**Interfaces:**
- Produces: explicit `TimeProvider` dependencies in Job, repository, scheduler, and Redis lock services.
- Produces: provider-aware `Task.Delay(delay, timeProvider, cancellationToken)` calls.
- Consumes: unchanged `IJobTimeCalculator.GetNextRunTime(Job, DateTimeOffset)`.

- [ ] **Step 1: Make Job repository tests deterministic**

Update `JobStoreTests`:

~~~csharp
var utcNow = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);
var timeProvider = new TestTimeProvider(utcNow);
var store = new JobRepo(dbContext, timeProvider);
~~~

Assert affected job/log `CreateTime` and `UpdateTime` values equal `utcNow`.

Run:

~~~bash
dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-restore --filter FullyQualifiedName~JobStoreTests
~~~

Expected: FAIL until `JobRepo` accepts and uses `TimeProvider`.

- [ ] **Step 2: Convert JobAppService to an explicit constructor**

Replace its primary constructor with private readonly fields and an explicit constructor containing the existing seven dependencies plus `TimeProvider timeProvider`. Use `_timeProvider.GetUtcNow()` for create, update, initial `NextRunTime`, and `ResolveNextRunTime`.

The create path captures one value:

~~~csharp
var now = _timeProvider.GetUtcNow();
~~~

Use it for `CreateTime`, `UpdateTime`, and the initial scheduling calculation.

- [ ] **Step 3: Convert JobRepo clocks**

Add `TimeProvider` to the explicit constructor. Replace all status/log clock reads with `_timeProvider.GetUtcNow()`. In `AddExecutionLogAsync`, capture one timestamp and use it for both metadata fields.

- [ ] **Step 4: Convert JobHostedService to an explicit constructor**

Replace its primary constructor with private fields and an explicit constructor containing the existing five dependencies plus `TimeProvider timeProvider`. Replace every current-time read with `_timeProvider.GetUtcNow()`.

Replace scheduler delays with:

~~~csharp
await Task.Delay(delay, _timeProvider, cancellationToken);
var delayTask = Task.Delay(delay, _timeProvider, cancellationToken);
~~~

Preserve wake-signal races and cancellation behavior.

- [ ] **Step 5: Propagate TimeProvider through RedisLock**

Inject `TimeProvider` into `RedisProjectExecutionLock`, pass it into `RedisLock`, store it there, and pass it into the nested `RedisLockLease`.

Use:

~~~csharp
await Task.Delay(_retryDelay, _timeProvider, cancellationToken);
await Task.Delay(_renewInterval, _timeProvider, _renewCancellation.Token);
~~~

Do not change TTLs, retry intervals, scripts, or lock semantics.

- [ ] **Step 6: Convert remaining Job fixtures**

Change `JobRowVersionTests` fixture timestamps to explicit `DateTimeOffset` values or `TimeProvider.System.GetUtcNow()`. Pass `TestTimeProvider` to manually constructed changed services.

- [ ] **Step 7: Run Jobs tests**

Run:

~~~bash
dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-restore
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --no-restore --filter FullyQualifiedName~JobRowVersionTests
~~~

Expected: both commands pass.

---

### Task 8: Convert static tools and eliminate residual forbidden identifiers

**Files:**
- Modify: `src/server/Agw.Tools/Impl/Todo/TodoTaskStore.cs`
- Modify: `src/server/Agw.Tools/Impl/Samples/WeatherTool.cs`
- Modify: `src/server/Agw.Tools/ToolRegistryService.cs`
- Modify: `tests/Agw.Tools.Tests/ToolRegistryServiceTests.cs`
- Modify: `tests/Agw.Agents.Tests/ExecutionCommandHandlerTests.cs`
- Modify: `tests/Agw.Agents.Tests/AgentflowWorkflowCompilerTests.cs`
- Modify: `tests/Agw.Tasks.Tests/Agents/AgentResponseTests.cs`
- Modify: `tests/Agw.Tasks.Tests/Agents/AgentRuntimeServiceAppRelationTests.cs`

**Interfaces:**
- Produces: `TodoTaskItem.CreatedAt`/`UpdatedAt` as `DateTimeOffset`.
- Produces: tool metadata support for `DateTimeOffset` only.
- Uses: `TimeProvider.System` because these are static leaf utilities.

- [ ] **Step 1: Convert Todo timestamps**

Use:

~~~csharp
public DateTimeOffset CreatedAt { get; set; } = TimeProvider.System.GetUtcNow();
public DateTimeOffset UpdatedAt { get; set; } = TimeProvider.System.GetUtcNow();
~~~

In `Create`, capture one `var now = TimeProvider.System.GetUtcNow();` and assign both fields. In `Update`, set `UpdatedAt = TimeProvider.System.GetUtcNow()`.

- [ ] **Step 2: Convert the sample forecast clock**

Capture once before the loop:

~~~csharp
var today = TimeProvider.System.GetUtcNow();
~~~

Use `today.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)`.

- [ ] **Step 3: Remove DateTime from tool type metadata**

Delete only:

~~~csharp
if (type == typeof(DateTime)) return "DateTime";
~~~

Keep the `DateTimeOffset` branch. If its test covers the removed type, replace the case with `DateTimeOffset`.

- [ ] **Step 4: Convert remaining test values and timeout loops**

Use fixed `DateTimeOffset(..., TimeSpan.Zero)` values. For polling:

~~~csharp
var timeProvider = TimeProvider.System;
var timeout = timeProvider.GetUtcNow().AddSeconds(2);
while (_traces.Count < count && timeProvider.GetUtcNow() < timeout)
~~~

- [ ] **Step 5: Run strict residual scans**

Run:

~~~bash
rg -n --glob '*.cs' --glob '!**/Migrations/**' --glob '!**/bin/**' --glob '!**/obj/**' '\bDateTime\b' src/server tests
rg -n --glob '*.cs' --glob '!**/Migrations/**' --glob '!**/bin/**' --glob '!**/obj/**' '\b(DateTime|DateTimeOffset)\.(UtcNow|Now|Today)\b' src/server tests
rg -n --glob '*.cs' --glob '!**/bin/**' --glob '!**/obj/**' 'Task\.Delay\(' src/server
~~~

Expected:

- First command returns no matches.
- Second command returns no matches.
- Every third-command match passes a `TimeProvider` argument; no production two-argument delay remains.

- [ ] **Step 6: Run Tools tests and compile the solution**

Run:

~~~bash
dotnet test tests/Agw.Tools.Tests/Agw.Tools.Tests.csproj --no-restore
dotnet build Agw.slnx --no-restore
~~~

Expected: Tools tests and solution build pass.

---

### Task 9: Regenerate API artifacts and perform final verification

**Files:**
- Modify (generated): `src/clients/web/openapi.json`
- Modify (generated): `src/clients/web/src/api/openapi.d.ts`
- Verify only: `src/server/Agw.Infrastructure/Migrations/**`
- Verify only: `src/server/Agw.Infrastructure/Migrations/AgwDbContextModelSnapshot.cs`

**Interfaces:**
- Produces: tracked OpenAPI snapshot and TypeScript declarations generated from updated contracts.
- Consumes: all prior tasks.

- [ ] **Step 1: Start the updated backend**

Run in one terminal:

~~~bash
dotnet run --no-build --project src/server/Agw.Host --urls http://127.0.0.1:5015
~~~

Expected: host starts on `http://127.0.0.1:5015`. Keep it running only for schema retrieval.

- [ ] **Step 2: Refresh the OpenAPI snapshot**

Run from a second terminal:

~~~bash
curl --fail http://127.0.0.1:5015/openapi/v1.json --output src/clients/web/openapi.json
~~~

Expected: curl succeeds and writes current JSON. Stop the temporary host afterward.

- [ ] **Step 3: Regenerate TypeScript declarations**

Run the actual package script:

~~~bash
pnpm --dir src/clients/web install --frozen-lockfile
pnpm --dir src/clients/web gen:api
pnpm --dir src/clients/web format:check
~~~

Expected: generation and formatting checks pass. `package.json` exposes `gen:api`; do not invoke the stale documented `gen:openapi` name.

- [ ] **Step 4: Verify migration history is untouched**

Run:

~~~bash
git diff --name-only -- src/server/Agw.Infrastructure/Migrations
~~~

Expected: no output. The final handoff must state that persisted CLR timestamp changes need a follow-up EF migration assessment, especially for PostgreSQL/MySQL, without generating or applying one here.

- [ ] **Step 5: Run each unaffected test project as a clean gate**

Run:

~~~bash
dotnet test tests/Agw.Shared.Tests/Agw.Shared.Tests.csproj --no-restore
dotnet test tests/Agw.A2A.Tests/Agw.A2A.Tests.csproj --no-restore
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore
dotnet test tests/Agw.Setup.Tests/Agw.Setup.Tests.csproj --no-restore
dotnet test tests/Agw.Skills.Tests/Agw.Skills.Tests.csproj --no-restore
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --no-restore
dotnet test tests/Agw.Tools.Tests/Agw.Tools.Tests.csproj --no-restore
dotnet test tests/Agw.Host.Tests/Agw.Host.Tests.csproj --no-restore
dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-restore
~~~

Expected: all nine commands pass.

- [ ] **Step 6: Re-run the full baseline command**

Run:

~~~bash
dotnet test Agw.slnx --no-restore
~~~

Expected: the only failures are the four baseline `Agw.Files.Tests.PathSecurityServiceTests` named in Global Constraints. Any additional failure is a regression.

- [ ] **Step 7: Run final build, scans, and diff checks**

Run:

~~~bash
dotnet build Agw.slnx --no-restore
rg -n --glob '*.cs' --glob '!**/Migrations/**' --glob '!**/bin/**' --glob '!**/obj/**' '\bDateTime\b' src/server tests
rg -n --glob '*.cs' --glob '!**/Migrations/**' --glob '!**/bin/**' --glob '!**/obj/**' '\b(DateTime|DateTimeOffset)\.(UtcNow|Now|Today)\b' src/server tests
git diff --check
git status --short
~~~

Expected: build succeeds; both scans return no matches; `git diff --check` succeeds; status lists only files required by this plan, the design spec, and this plan.

## Completion Handoff

Report:

- Worktree path and branch.
- Number of production/test files changed.
- Zero remaining non-generated `DateTime` or direct framework-clock matches.
- Targeted test results and the unchanged four baseline Files failures.
- OpenAPI generation result.
- Confirmation that no migration was created/applied and a follow-up migration assessment remains.
- Confirmation that no commit was created unless separately authorized.
