# Distributed Lock Configuration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an independent distributed-lock configuration that overrides database-derived defaults while keeping SQLite process-local and PostgreSQL distributed behavior when the section is absent.

**Architecture:** Resolve optional `DistributedLock` settings together with the live database initialization state, then route `IProjectExecutionLock` to either the existing in-memory lock or a provider-neutral Medallion adapter. Keep provider construction in Infrastructure so future backends require no Jobs-layer changes.

**Tech Stack:** .NET 10, ASP.NET Core configuration/options, Medallion.Threading, DistributedLock.Postgres, xUnit

## Global Constraints

- `DistributedLock:Provider` supports `inmemory` and `postgres`.
- A missing or null lock provider falls back to `Database:Provider`: SQLite selects `inmemory`; PostgreSQL selects `postgres`.
- A PostgreSQL lock with no lock-specific connection string reuses the live database connection string.
- Keep `IProjectExecutionLock` as the Jobs-facing boundary and preserve dynamic post-Setup behavior.
- Do not introduce a provider registry until a second distributed backend is added.
- Do not create a Git commit unless the user explicitly requests one.

---

### Task 1: Resolve Effective Distributed-Lock Configuration

**Files:**
- Create: `src/server/Agw.Infrastructure/Configuration/DistributedLockSettings.cs`
- Create: `src/server/Agw.Infrastructure/Configuration/DistributedLockSettingsResolver.cs`
- Modify: `src/server/Agw.Shared/Exceptions/ErrorCodes.cs`
- Create: `tests/Agw.Jobs.Tests/DistributedLockSettingsResolverTests.cs`

**Interfaces:**
- Consumes: `DatabaseProviderResolver.Normalize(string?)` and `ErrorCodes.UnsupportedDatabaseProvider`.
- Produces: `DistributedLockSettings` with `SectionName`, `Provider`, and `ConnectionString`; `DistributedLockSettingsResolver.Resolve(DistributedLockSettings?, string, string)` returning normalized effective settings.

- [ ] **Step 1: Write failing resolver tests**

Cover missing-provider fallback for SQLite and PostgreSQL, explicit `inmemory` and `postgres`, explicit connection-string precedence, database connection-string reuse, and an unsupported explicit provider returning `ErrorCodes.UnsupportedDistributedLockProvider`.

```csharp
var resolved = DistributedLockSettingsResolver.Resolve(
    new DistributedLockSettings { Provider = "postgres", ConnectionString = "" },
    "sqlite",
    "Host=database");

Assert.Equal("postgres", resolved.Provider);
Assert.Equal("Host=database", resolved.ConnectionString);
```

- [ ] **Step 2: Verify the tests fail for missing production types**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-restore --filter FullyQualifiedName~DistributedLockSettingsResolverTests`

Expected: build failure because `DistributedLockSettings`, `DistributedLockSettingsResolver`, and `UnsupportedDistributedLockProvider` do not exist.

- [ ] **Step 3: Implement minimal settings and resolver**

Normalize explicit values with `Trim().ToLowerInvariant()`. Return `inmemory` with an empty connection string. Return `postgres` with the explicit nonblank connection string or the database connection string. Throw:

```csharp
throw new AgwException(
    ErrorCodes.UnsupportedDistributedLockProvider,
    $"Distributed lock provider '{provider}' is not supported.");
```

- [ ] **Step 4: Verify resolver tests pass**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-restore --filter FullyQualifiedName~DistributedLockSettingsResolverTests`

Expected: all resolver tests pass.

### Task 2: Generalize the Lock Adapter and Router

**Files:**
- Delete: `src/server/Agw.Infrastructure/Jobs/DatabaseProjectExecutionLock.cs`
- Delete: `src/server/Agw.Infrastructure/Jobs/PostgresProjectExecutionLock.cs`
- Create: `src/server/Agw.Infrastructure/Jobs/ProjectExecutionLockRouter.cs`
- Create: `src/server/Agw.Infrastructure/Jobs/DistributedProjectExecutionLock.cs`
- Replace: `tests/Agw.Jobs.Tests/DatabaseProjectExecutionLockTests.cs` with `tests/Agw.Jobs.Tests/ProjectExecutionLockRouterTests.cs`

**Interfaces:**
- Consumes: `IServerInitializationState`, `IOptionsMonitor<DistributedLockSettings>`, `InMemoryProjectExecutionLock`, and `Func<string, string, IDistributedLockProvider>`.
- Produces: `ProjectExecutionLockRouter : IProjectExecutionLock` and `DistributedProjectExecutionLock : IProjectExecutionLock`.

- [ ] **Step 1: Write failing router tests against the new names**

Test explicit `inmemory`, database-derived SQLite fallback, explicit PostgreSQL over SQLite, database-derived PostgreSQL fallback, explicit connection-string precedence, options changes without restart, database changes without restart, provider reuse for an unchanged effective configuration, provider replacement when provider/connection changes, lock-name mapping, cancellation propagation, and handle disposal.

```csharp
var projectLock = new ProjectExecutionLockRouter(
    state,
    optionsMonitor,
    new InMemoryProjectExecutionLock(),
    (provider, connectionString) => recordingProvider);
```

- [ ] **Step 2: Verify the router tests fail for missing new types**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-restore --filter FullyQualifiedName~ProjectExecutionLockRouterTests`

Expected: build failure because `ProjectExecutionLockRouter` and `DistributedProjectExecutionLock` do not exist.

- [ ] **Step 3: Implement the provider-neutral adapter**

Map each project to `agw:jobs:project-lock:{projectId:D}` and call:

```csharp
return await _lockProvider.AcquireLockAsync(lockName, cancellationToken: cancellationToken);
```

- [ ] **Step 4: Implement the router**

Resolve settings on every acquisition. Delegate `inmemory` directly. Cache the distributed adapter under a private synchronization object using the effective provider and connection string as the cache key.

- [ ] **Step 5: Verify router tests pass**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-restore --filter FullyQualifiedName~ProjectExecutionLockRouterTests`

Expected: all router tests pass.

### Task 3: Wire Configuration and PostgreSQL Provider Construction

**Files:**
- Modify: `src/server/Agw.Infrastructure/DependencyInjection.cs`
- Modify: `src/server/Agw.Host/appsettings.json`
- Modify: `tests/Agw.Jobs.Tests/InfrastructureRegistrationTests.cs`

**Interfaces:**
- Consumes: `DistributedLockSettings.SectionName`, `ProjectExecutionLockRouter`, and `PostgresDistributedSynchronizationProvider`.
- Produces: options binding, the `(provider, connectionString)` Medallion provider factory, and `IProjectExecutionLock` registration.

- [ ] **Step 1: Update DI tests first**

Assert `IProjectExecutionLock` resolves to `ProjectExecutionLockRouter`, `DistributedLockSettings` options are registered, and the two-argument provider factory is registered. Assert an explicitly unsupported lock provider fails during `AddInfrastructure` with `UnsupportedDistributedLockProvider`.

- [ ] **Step 2: Verify DI tests fail**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-restore --filter FullyQualifiedName~InfrastructureRegistrationTests`

Expected: failure because DI still registers the database-named router and one-argument factory.

- [ ] **Step 3: Update infrastructure registration**

Bind `DistributedLockSettings`, validate a nonblank configured provider during registration, register `Func<string, string, IDistributedLockProvider>`, and switch `postgres` to `PostgresDistributedSynchronizationProvider`. Register `ProjectExecutionLockRouter` as the singleton `IProjectExecutionLock`.

- [ ] **Step 4: Add the empty default configuration section**

```json
"DistributedLock": {
  "Provider": null,
  "ConnectionString": ""
}
```

Blank values intentionally preserve database-derived behavior and keep secrets out of the checked-in file.

- [ ] **Step 5: Verify DI and Jobs tests pass**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-restore`

Expected: all Jobs tests pass.

### Task 4: Synchronize Operational Documentation

**Files:**
- Modify: `AGENTS.md`
- Modify: `CLAUDE.md`
- Modify: `README.md`
- Modify: `README.zh-CN.md`
- Modify: `docs/1.Development.md`
- Modify: `docs/2.Architecture.md`
- Modify: `docs/4.Deployment.md`

**Interfaces:**
- Consumes: the final configuration names and fallback rules.
- Produces: consistent setup, development, architecture, and deployment guidance.

- [ ] **Step 1: Replace database-coupled lock wording**

Document that `IProjectExecutionLock` routes by the independent lock configuration and only falls back to the database provider when `DistributedLock:Provider` is absent or blank.

- [ ] **Step 2: Add configuration examples**

Document an explicit PostgreSQL lock with an empty connection string and explain that it reuses the database connection string. Document that explicit `inmemory` is single-replica only.

- [ ] **Step 3: Check for stale names and contradictory provider claims**

Run: `rg -n "DatabaseProjectExecutionLock|PostgresProjectExecutionLock|RedisProjectExecutionLock|DistributedLock|distributed lock|分布式锁" AGENTS.md CLAUDE.md README.md README.zh-CN.md docs src/server/Agw.Agents/Execution/README.md`

Expected: no stale class names; configuration and fallback descriptions agree.

### Task 5: Final Verification

**Files:**
- Verify only; no planned production edits.

**Interfaces:**
- Consumes: all preceding tasks.
- Produces: build and test evidence.

- [ ] **Step 1: Check formatting and dependency boundaries**

Run: `git diff --check`

Expected: no whitespace errors.

Run: `dotnet list src/server/Agw.Infrastructure/Agw.Infrastructure.csproj package --include-transitive | rg "DistributedLock|Redis|MySql|Npgsql"`

Expected: PostgreSQL distributed-lock packages are present; Redis and MySQL packages are absent.

- [ ] **Step 2: Build the solution**

Run: `dotnet build Agw.slnx --no-restore`

Expected: build succeeds with zero errors.

- [ ] **Step 3: Run focused tests**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-build --no-restore`

Expected: all Jobs tests pass.

- [ ] **Step 4: Run the complete backend suite**

Run: `dotnet test Agw.slnx --no-build --no-restore`

Expected: no new failures; the four user-approved pre-existing `Agw.Files.Tests.PathSecurityServiceTests` failures may remain.

- [ ] **Step 5: Review the final diff without committing**

Run: `git status --short && git diff --stat && git diff --cached --stat`

Expected: only distributed-lock migration/configuration work and the already approved Redis/MySQL removal changes are present. Do not stage or commit automatically.
