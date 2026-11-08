# Extension database authoring

Reference `Agw.Extensions.Sdk.EntityFrameworkCore` from a Server extension that owns business tables. The extension package must include its migrations assembly and provider dependencies. The host loads this EF support assembly and its provider privately for each extension generation.

## Register a context

Register one `ExtensionDbContext` and a marker type from the assembly that contains its migrations:

```csharp
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.Database;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public sealed class CalendarExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        registration.RegisterDbContext<CalendarDbContext, CalendarMigrationsMarker>();
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class CalendarDbContext : ExtensionDbContext
{
    public CalendarDbContext(
        DbContextOptions<CalendarDbContext> options,
        IExtensionDatabaseIdentity identity,
        IExtensionUserContext userContext,
        TimeProvider timeProvider
    ) : base(options, identity, userContext, timeProvider)
    {
    }

    public DbSet<Appointment> Appointments => Set<Appointment>();

    protected override void ConfigureExtensionModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Appointment>(entity =>
        {
            entity.ToTable("appointment");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.Title).HasColumnName("title");
        });
    }
}

public sealed class CalendarMigrationsMarker { }

public sealed class Appointment : IExtensionOwnedEntity
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? CreateBy { get; set; }
    public DateTimeOffset CreateTime { get; set; }
    public string? UpdateBy { get; set; }
    public DateTimeOffset? UpdateTime { get; set; }
}
```

The host supplies the context identity, user context, `TimeProvider`, provider, connection, and transaction guards. Context options and model caches are generation-scoped.

The optional EF SDK and `Microsoft.EntityFrameworkCore.*` assemblies load privately for each generation. The host shares the low-level `Microsoft.Data.Sqlite` driver and `SQLitePCLRaw.*` interop assemblies through the Default `AssemblyLoadContext`; their process-wide callbacks and native transport outlive individual generations.

可选 EF SDK 和 `Microsoft.EntityFrameworkCore.*` assemblies 会在每个 generation 中私有加载。宿主通过 Default `AssemblyLoadContext` 共享底层 `Microsoft.Data.Sqlite` driver 和 `SQLitePCLRaw.*` interop assemblies；它们的进程级回调和 native transport 生命周期长于单个 generation。

## Transaction and migration locks

Each business transaction takes a shared database lock and validates the exact local extension generation and active scope before work and again before commit. The lock stays held through commit, rollback, or disposal; access through an inactive or disposed generation fails. Keep explicit transactions scoped to the smallest unit of work and dispose them even after a failed save.

The host takes the matching exclusive lock for the full migration batch on one open connection. SQLite uses a process-wide reader/writer lock per database and extension, plus file ownership for migrations across processes. Its migration lock table uses the extension prefix; abandoned lock rows are cleared only while holding exclusive migration ownership. Implicit read transactions use deferred SQLite transactions and complete asynchronously when an async reader closes. PostgreSQL uses a shared advisory lock tied to the business transaction and an exclusive session advisory lock for schema changes. The host checks the prepared local generation and shared enablement while holding that lock. Start migrations through the extension runtime so it can validate and lock them.

业务事务和 schema 准备使用当前本地 generation 的身份与作用域校验，并保留用户隔离及完整事务期间的数据库锁。扩展目录更新在停机期间进行；扩展作者负责新代码与保留的数据库 schema、migration history 和执行状态的兼容性。

## Ownership and audit

Every table entity implements `IExtensionOwnedEntity`. The SDK stamps `CreateBy` and `CreateTime` when adding a row, preserves `CreateBy`, and stamps `UpdateBy` and `UpdateTime` on updates. Queries are filtered to the current user and return no rows when the user context is missing.

Model child rows with `IExtensionOwnedChild<TRoot, TRootKey>` and a `RootId` property. The SDK checks the persisted root owner before saving a child. EF navigation metadata can be used for joins and `Include`; generated migrations omit foreign-key constraints.

Use filtered LINQ queries and `SaveChanges` for business data. `IgnoreQueryFilters`, `ExecuteUpdate`, `ExecuteDelete`, SQL query roots, raw SQL commands, and external transactions are rejected. Internal ownership queries have separate compiled-query cache keys. Loaded extensions execute trusted .NET code inside the Server process; these SDK checks govern supported database access, and do not create a process security boundary.

业务数据通过带有用户过滤的 LINQ 查询及 `SaveChanges` 访问。SDK 拒绝 `IgnoreQueryFilters`、批量更新、批量删除、原生 SQL 查询与命令，以及外部事务。内部所有权查询使用独立的编译查询缓存标识。扩展在 Server 进程内执行受信任的 .NET 代码，SDK 检查适用于其支持的数据库访问方式。

## Tables and migrations

Use short, lowercase `snake_case` table names in `ToTable`. The runtime prefixes them with `zext_<extensionIdN>_`; the prefix is stable for that extension. Migration operations must use the full prefixed table name. The migration history table is generation-managed and uses the same extension prefix.

Include SQLite and PostgreSQL migrations in the extension release. Use structured EF migration operations for schema and data changes (`CreateTable`, `AddColumn`, `CreateIndex`, `InsertData`, `UpdateData`, and `DeleteData`). Index names must be extension-owned: use the generated `ix_zext_<extensionIdN>_...` form or an explicit name beginning with `zext_<extensionIdN>_`. Primary and unique constraint names also require their extension prefix (`pk_zext_<extensionIdN>_...` and `uq_zext_<extensionIdN>_...`). Tables, columns, indexes, constraints, temporary identifiers, and operations are checked against provider limits. Column types must match the provider mapping for their CLR types and configured facets; collation names are restricted to identifiers. Validation covers the actual operations and target model used by the SQL generator, including SQLite table rebuilds.

Migrations cannot contain raw SQL, foreign keys, sequences, schemas, check constraints, SQL defaults, computed SQL, or SQL index filters. Entity mappings to views, SQL queries, functions, stored procedures, or sequences are rejected. Migration history remains with retained business data so a compatible release can be reinstalled without rebuilding or downgrading the schema.

SQLite rebuilds use generation-prefixed temporary identifiers. Keep table and index names short enough for both providers; model validation applies the configured provider's identifier limit before migration.

## EF Core version

The extension runtime owns the exact EF Core metrics meter for each collectible generation and disposes it during unload. The capture uses EF Core 10.0's public `EntityFrameworkMetricsData` initializer (`EF9101`); recheck that initializer and meter publication when upgrading EF Core.

扩展运行时为每个 collectible generation 持有对应的 EF Core 指标 Meter，并在卸载时释放。该处理使用 EF Core 10.0 的公开 `EntityFrameworkMetricsData` 初始化入口（`EF9101`）；升级 EF Core 时需重新核对该入口和 Meter 发布过程。
