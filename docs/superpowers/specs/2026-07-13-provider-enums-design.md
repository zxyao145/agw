# Provider Enums Design

## Goal

Replace database-provider and distributed-lock-provider strings with two enums throughout runtime code while retaining lowercase string configuration and server-state persistence.

## Types

Keep the cross-module database enum in `Agw.Shared.Configuration`:

```csharp
public enum DatabaseProvider
{
    Sqlite,
    Postgres
}
```

Place the infrastructure-local lock enum beside `DistributedLockSettings` in
`Agw.Infrastructure.Configuration`:

```csharp
public enum DistributedLockProvider
{
    InMemory,
    Postgres
}
```

`DatabaseSettings.Provider`, `SetupRequest.Provider`, and `IServerInitializationState.DatabaseProvider` use `DatabaseProvider`. `DistributedLockSettings.Provider` uses `DistributedLockProvider?`; `null` means infer the lock provider from the database provider.

The distributed-lock provider factory and router cache use `DistributedLockProvider`, so provider identity is not converted back to a string inside runtime code.

## Configuration Boundaries

ASP.NET Core configuration continues to use lowercase strings: `sqlite`, `postgres`, and `inmemory`. Enum binding is case-insensitive. The former `postgresql` alias is intentionally removed.

Before binding settings, Infrastructure validates the raw configured provider values and throws the existing `AgwException` error codes for unsupported database or lock providers. This preserves intentional configuration failures instead of exposing framework binding exceptions.

`DistributedLock:Provider` is represented as `null` in the default JSON configuration. A missing or null value retains database-provider fallback. PostgreSQL locks with no lock-specific connection string continue to reuse the database connection string.

## Setup And Persistence

The Setup request model uses `DatabaseProvider`, allowing MVC model binding to reject unsupported form values. The setup form continues to post `sqlite` and `postgres`.

`server-state.json` adds `JsonStringEnumConverter` with camel-case naming. Existing states containing `"sqlite"` or `"postgres"` continue to load, and newly persisted states keep the same lowercase textual representation rather than numeric enum values.

## Removed String Logic

`DatabaseProviderResolver.Normalize` is replaced by boundary parsing that returns `DatabaseProvider`. Database connection selection, EF provider selection, lock fallback, and lock routing use enum switches or comparisons. No runtime provider-name string comparisons remain.

## Verification

Tests cover enum configuration binding, rejected `postgresql`/`mysql`/`redis` values, typed connection-string resolution, typed Setup persistence and reload, camel-case server-state JSON, database-derived lock fallback, explicit lock selection, typed provider-factory calls, and runtime provider changes.
