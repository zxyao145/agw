# Distributed Lock Configuration Design

## Goal

Decouple project execution locking from the database provider while preserving the current default behavior: SQLite uses a process-local lock and PostgreSQL uses PostgreSQL advisory locks.

## Configuration

Add an optional `DistributedLock` section:

```json
{
  "DistributedLock": {
    "Provider": null,
    "ConnectionString": ""
  }
}
```

`Provider` supports `inmemory` and `postgres`. Missing or null `Provider` means the lock provider is inferred from the current database provider:

- `sqlite` selects `inmemory`.
- `postgres` selects `postgres`.

When the effective lock provider is `postgres`, a missing or whitespace-only `DistributedLock:ConnectionString` reuses the current database connection string. An explicit `inmemory` provider allows a PostgreSQL-backed installation to opt out of distributed locking, with the operational limitation that it must run as a single replica.

Unsupported explicit lock providers fail with `AgwException` and a dedicated `UnsupportedDistributedLockProvider` error code. Unsupported database providers continue to use `UnsupportedDatabaseProvider`.

## Architecture

`IProjectExecutionLock` remains the application-facing anti-corruption boundary. `JobHostedService` remains unaware of the concrete locking backend.

`DistributedLockSettingsResolver` combines the optional lock settings with the live `IServerInitializationState` database settings and returns a normalized effective configuration. `ProjectExecutionLockRouter` reads that effective configuration for each acquisition, delegates process-local locking to `InMemoryProjectExecutionLock`, and delegates distributed locking to `DistributedProjectExecutionLock`.

`DistributedProjectExecutionLock` only maps the project identifier to the stable lock name `agw:jobs:project-lock:{projectId}` and invokes Medallion's `IDistributedLockProvider`. It does not contain PostgreSQL-specific behavior. The router caches the distributed adapter by effective provider and connection string, replacing it when either value changes.

The provider factory remains a small switch in infrastructure registration. Adding Redis, ZooKeeper, or a filesystem provider later requires adding its NuGet package, accepting its provider name in the resolver, and adding one factory branch; the Jobs module and `IProjectExecutionLock` consumers remain unchanged.

## Runtime Behavior

The router reads `IOptionsMonitor<DistributedLockSettings>.CurrentValue` on each acquisition. This supports configuration reload where the host configuration source supports it. When the independent section is absent, the router reads the live database provider and connection string from `IServerInitializationState`, preserving the existing post-Setup switch without restarting.

## Verification

Tests cover explicit in-memory selection, explicit PostgreSQL selection, database-provider fallback, database connection-string reuse, explicit lock connection-string precedence, unsupported providers, runtime configuration changes, stable lock naming, dependency injection registration, and provider caching/replacement.
