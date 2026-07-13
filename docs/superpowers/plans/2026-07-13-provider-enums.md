# Provider Enums Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace database and distributed-lock provider strings with two enums across configuration, Setup, initialization state, routing, and provider construction.

**Architecture:** Define the cross-module database enum in Shared. Define the infrastructure-local distributed-lock enum beside `DistributedLockSettings`. Keep string parsing only at configuration and MVC/JSON boundaries; use typed values everywhere in runtime code.

**Tech Stack:** .NET 10, ASP.NET Core configuration/options/MVC, System.Text.Json, EF Core, Medallion.Threading, xUnit

## Global Constraints

- Use exactly two provider enums: `DatabaseProvider` and `DistributedLockProvider`.
- Supported database values are `sqlite` and `postgres`; supported lock values are `inmemory` and `postgres`.
- Do not preserve the former `postgresql` alias.
- A null distributed-lock provider falls back to the current database provider.
- Persist enum values as camel-case JSON strings, not numbers.
- Unsupported configured providers must continue to throw the existing `AgwException` error codes.
- Do not create a Git commit unless the user explicitly requests one.

---

### Task 1: Add Provider Enums And Typed Database Configuration

**Files:**
- Create: `src/server/Agw.Shared/Configuration/DatabaseProvider.cs`
- Create: `src/server/Agw.Infrastructure/Configuration/DistributedLockProvider.cs`
- Modify: `src/server/Agw.Infrastructure/Configuration/DatabaseSettings.cs`
- Modify: `src/server/Agw.Infrastructure/Configuration/DatabaseProviderResolver.cs`
- Modify: `src/server/Agw.Infrastructure/Configuration/DatabaseConnectionStringResolver.cs`
- Modify: `tests/Agw.Jobs.Tests/DatabaseProviderResolverTests.cs`
- Modify: `tests/Agw.Jobs.Tests/DatabaseConnectionStringResolverTests.cs`

**Interfaces:**
- Produces: `DatabaseProvider.Sqlite`, `DatabaseProvider.Postgres`, `DistributedLockProvider.InMemory`, and `DistributedLockProvider.Postgres`.
- Produces: `DatabaseProviderResolver.Parse(string)` returning `DatabaseProvider`.
- Produces: `DatabaseConnectionStringResolver.Resolve(DatabaseProvider, string, AgwDataPaths)`.

- [ ] **Step 1: Change tests to require enum results and arguments**

```csharp
Assert.Equal(DatabaseProvider.Sqlite, DatabaseProviderResolver.Parse("sqlite"));
Assert.Equal(DatabaseProvider.Postgres, DatabaseProviderResolver.Parse("postgres"));
Assert.Throws<AgwException>(() => DatabaseProviderResolver.Parse("postgresql"));
```

- [ ] **Step 2: Run focused tests and verify compile failure**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-restore --filter "FullyQualifiedName~DatabaseProviderResolverTests|FullyQualifiedName~DatabaseConnectionStringResolverTests"`

Expected: compile failure because the enums and typed APIs do not exist.

- [ ] **Step 3: Add enums and typed database APIs**

Make `DatabaseSettings.Provider` default to `DatabaseProvider.Sqlite`. Parse only `sqlite` and `postgres`; reject all other strings with `ErrorCodes.UnsupportedDatabaseProvider`. Select SQLite connection-string path handling with an enum comparison.

- [ ] **Step 4: Run focused tests and verify pass**

Run the Step 2 command. Expected: all focused tests pass.

### Task 2: Type Distributed-Lock Resolution And Routing

**Files:**
- Modify: `src/server/Agw.Infrastructure/Configuration/DistributedLockSettings.cs`
- Modify: `src/server/Agw.Infrastructure/Configuration/DistributedLockSettingsResolver.cs`
- Modify: `src/server/Agw.Infrastructure/Jobs/ProjectExecutionLockRouter.cs`
- Modify: `tests/Agw.Jobs.Tests/DistributedLockSettingsResolverTests.cs`
- Modify: `tests/Agw.Jobs.Tests/ProjectExecutionLockRouterTests.cs`

**Interfaces:**
- Consumes: the Shared database enum and Infrastructure distributed-lock enum from Task 1.
- Produces: nullable `DistributedLockSettings.Provider`, typed effective resolution, and `Func<DistributedLockProvider, string, IDistributedLockProvider>`.

- [ ] **Step 1: Change lock tests to enum providers and a typed factory**

```csharp
var settings = new DistributedLockSettings
{
    Provider = DistributedLockProvider.Postgres
};
```

Assert fallback maps `DatabaseProvider.Sqlite` to `InMemory` and `DatabaseProvider.Postgres` to `Postgres`. Remove the former alias test.

- [ ] **Step 2: Run lock tests and verify compile failure**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-restore --filter "FullyQualifiedName~DistributedLockSettingsResolverTests|FullyQualifiedName~ProjectExecutionLockRouterTests"`

Expected: compile failure because production lock APIs still use strings.

- [ ] **Step 3: Implement enum-only runtime routing**

Switch the resolver, router cache, comparisons, and factory delegate to the enum types. Preserve connection-string fallback and cache replacement behavior.

- [ ] **Step 4: Run lock tests and verify pass**

Run the Step 2 command. Expected: all lock tests pass.

### Task 3: Type Setup And Initialization-State Persistence

**Files:**
- Modify: `src/server/Agw.Setup/Contracts/SetupRequest.cs`
- Modify: `src/server/Agw.Setup/Controllers/SetupController.cs`
- Modify: `src/server/Agw.Setup/Services/SetupInitializationService.cs`
- Modify: `src/server/Agw.Setup/Services/JsonInitializationStateStore.cs`
- Modify: `src/server/Agw.Shared/Runtime/IServerInitializationState.cs`
- Modify: `tests/Agw.Setup.Tests/JsonInitializationStateStoreTests.cs`

**Interfaces:**
- Consumes: `DatabaseProvider`.
- Produces: typed Setup requests and initialization state with camel-case string JSON persistence.

- [ ] **Step 1: Update persistence tests first**

Construct Setup requests with `DatabaseProvider.Sqlite`, assert `IServerInitializationState.DatabaseProvider` is typed, assert the state file contains `"provider": "sqlite"`, and verify reloading the file succeeds.

- [ ] **Step 2: Run Setup tests and verify compile failure**

Run: `dotnet test tests/Agw.Setup.Tests/Agw.Setup.Tests.csproj --no-restore`

Expected: compile failure because Setup and initialization-state contracts still use strings.

- [ ] **Step 3: Implement typed Setup and JSON conversion**

Use `DatabaseProvider.Sqlite` as the request default. Remove normalization from initialization. Configure `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` in the state store and switch EF provider selection directly on the enum.

- [ ] **Step 4: Run Setup tests and verify pass**

Run the Step 2 command. Expected: all Setup tests pass.

### Task 4: Bind And Validate Enum Configuration In Infrastructure

**Files:**
- Modify: `src/server/Agw.Infrastructure/DependencyInjection.cs`
- Modify: `src/server/Agw.Infrastructure/Data/AgwDbContextDesignTimeFactory.cs`
- Modify: `src/server/Agw.Host/appsettings.json`
- Modify: `tests/Agw.Jobs.Tests/InfrastructureRegistrationTests.cs`

**Interfaces:**
- Consumes: typed settings, resolvers, router, and provider factory.
- Produces: case-insensitive lowercase configuration binding and startup validation using `AgwException`.

- [ ] **Step 1: Change DI tests to assert enum binding and typed factory registration**

Assert lowercase `postgres` binds to `DistributedLockProvider.Postgres`, the factory service type is `Func<DistributedLockProvider, string, IDistributedLockProvider>`, and `postgresql`, `mysql`, and `redis` fail with the correct error codes.

- [ ] **Step 2: Run DI tests and verify failure**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-restore --filter FullyQualifiedName~InfrastructureRegistrationTests`

Expected: compile or assertion failure while DI still uses string providers.

- [ ] **Step 3: Implement typed registration and raw boundary validation**

Validate raw provider strings before binding. Register the typed factory and use enum switches for EF and Medallion provider selection. Set the default lock provider in `appsettings.json` to `null`.

- [ ] **Step 4: Run all Jobs tests**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-restore`

Expected: all Jobs tests pass.

### Task 5: Synchronize Documentation And Verify

**Files:**
- Modify only documentation snippets that show an empty distributed-lock provider.
- Verify all implementation files from Tasks 1-4.

**Interfaces:**
- Produces: accurate enum-backed configuration examples and verification evidence.

- [ ] **Step 1: Replace empty lock-provider JSON examples with null**

Keep the documented external values lowercase and document that `null` or a missing value triggers database fallback.

- [ ] **Step 2: Check for runtime string comparisons and stale alias claims**

Run: `rg -n 'Provider == "|Provider = "|Normalize\(|postgresql' src/server tests/Agw.Jobs.Tests tests/Agw.Setup.Tests --glob '*.cs'`

Expected: no provider runtime string comparisons or supported-alias tests remain.

- [ ] **Step 3: Check whitespace and dependencies**

Run: `git diff --check` and `git diff --cached --check`.

Expected: no whitespace errors.

- [ ] **Step 4: Build**

Run: `dotnet build Agw.slnx --no-restore`.

Expected: build succeeds with zero errors.

- [ ] **Step 5: Run focused and full tests**

Run: `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --no-build --no-restore`.

Expected: all Jobs tests pass.

Run: `dotnet test Agw.slnx --no-build --no-restore`.

Expected: no new failures; the four user-approved pre-existing `Agw.Files.Tests.PathSecurityServiceTests` failures may remain.

- [ ] **Step 6: Leave changes uncommitted**

Run: `git status --short`.

Expected: the enum refactor appears alongside the existing Redis/MySQL removal and distributed-lock work. Do not stage or commit automatically.
