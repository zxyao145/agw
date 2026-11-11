using System.Linq.Expressions;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Agw.Extensions.Sdk.Database;

public abstract class ExtensionDbContext : DbContext
{
    private readonly IExtensionDatabaseIdentity _identity;
    private readonly IExtensionUserContext _userContext;
    private readonly TimeProvider _timeProvider;
    private readonly Guid? _invocationId;

    protected ExtensionDbContext(
        DbContextOptions options,
        IExtensionDatabaseIdentity identity,
        IExtensionUserContext userContext,
        TimeProvider timeProvider
    )
        : base(options)
    {
        if (identity is null || userContext is null || timeProvider is null)
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }

        if (identity.ExtensionId == Guid.Empty || identity.GenerationId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }

        if (!string.Equals(identity.TablePrefix, GetTablePrefix(identity.ExtensionId), StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }

        _identity = identity;
        _userContext = userContext;
        _timeProvider = timeProvider;
        _invocationId = userContext.InvocationId;
        Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
    }

    public IExtensionDatabaseIdentity ExtensionDatabaseIdentity => _identity;

    public string? CurrentUserId => _userContext.UserId;

    internal Guid? InvocationId => _invocationId;

    internal bool IsCheckingOwnership { get; private set; }

    internal bool IsScanningOutbox { get; private set; }

    internal bool IsReadingOwnedRecords { get; private set; }

    internal async Task<IReadOnlyList<ExtensionOwnedRecordIdentity>> ReadOwnedRecordsAsync<TRecord>(
        Guid? recordId,
        CancellationToken cancellationToken
    )
        where TRecord : class, IExtensionOwnedEntity
    {
        var entity =
            Model.FindEntityType(typeof(TRecord)) ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        var key = entity.FindPrimaryKey();
        if (key is null || key.Properties.Count != 1 || key.Properties[0].ClrType != typeof(Guid))
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        string keyName = key.Properties[0].Name;
        IsReadingOwnedRecords = true;
        try
        {
            IQueryable<TRecord> query = Set<TRecord>().AsNoTracking();
            if (CurrentUserId is null)
                query = query.IgnoreQueryFilters(["ExtensionUserScope"]);
            if (recordId is { } id)
                query = query.Where(record => EF.Property<Guid>(record, keyName) == id);
            return await query
                .Select(record => new ExtensionOwnedRecordIdentity(
                    EF.Property<Guid>(record, keyName),
                    record.CreateBy!
                ))
                .ToListAsync(cancellationToken);
        }
        finally
        {
            IsReadingOwnedRecords = false;
        }
    }

    internal async Task<ExtensionOutboxRow?> ReadNextOutboxAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        IsScanningOutbox = true;
        try
        {
            return await Set<ExtensionOutboxRow>()
                .IgnoreQueryFilters(["ExtensionUserScope"])
                .AsNoTracking()
                .Where(row =>
                    row.State == ExtensionOutboxState.Pending && (row.NextAttemptAt == null || row.NextAttemptAt <= now)
                )
                .OrderBy(row => row.CreateTime)
                .ThenBy(row => row.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        finally
        {
            IsScanningOutbox = false;
        }
    }

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ConfigureExtensionModel(modelBuilder);
        string dateTimeOffsetType =
            Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "timestamp with time zone";
        modelBuilder.UseExtensionMessaging(_identity.TablePrefix, dateTimeOffsetType);
        ApplyTableNamespace(modelBuilder);
        ApplyUserScopeFilters(modelBuilder);
    }

    protected virtual void ConfigureExtensionModel(ModelBuilder modelBuilder) { }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        using var transaction = Database.CurrentTransaction is null ? Database.BeginTransaction() : null;
        IsCheckingOwnership = true;
        try
        {
            ValidateAndStampOwnedEntities();
        }
        finally
        {
            IsCheckingOwnership = false;
        }
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        transaction?.Commit();
        return result;
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default
    )
    {
        await using var transaction = Database.CurrentTransaction is null
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;
        IsCheckingOwnership = true;
        try
        {
            await ValidateAndStampOwnedEntitiesAsync(cancellationToken);
        }
        finally
        {
            IsCheckingOwnership = false;
        }
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    public static string GetTablePrefix(Guid extensionId) => $"zext_{extensionId:N}_";

    internal DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

    private void ApplyTableNamespace(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName is not null && !tableName.StartsWith(_identity.TablePrefix, StringComparison.Ordinal))
            {
                entityType.SetTableName(_identity.TablePrefix + tableName);
            }
        }
    }

    private void ApplyUserScopeFilters(ModelBuilder modelBuilder)
    {
        foreach (
            var entityType in modelBuilder
                .Model.GetEntityTypes()
                .Where(entityType =>
                    entityType.BaseType is null && typeof(IExtensionOwnedEntity).IsAssignableFrom(entityType.ClrType)
                )
        )
        {
            var parameter = Expression.Parameter(entityType.ClrType, "entity");
            var createBy = Expression.Property(parameter, nameof(IExtensionOwnedEntity.CreateBy));
            var currentUser = Expression.Property(Expression.Constant(this), nameof(CurrentUserId));
            var filter = Expression.Lambda(
                Expression.AndAlso(
                    Expression.NotEqual(currentUser, Expression.Constant(null, typeof(string))),
                    Expression.Equal(createBy, currentUser)
                ),
                parameter
            );

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter("ExtensionUserScope", filter);
        }
    }

    private void ValidateAndStampOwnedEntities()
    {
        var userId = RequiredUserId();
        var now = _timeProvider.GetUtcNow();
        var entries = ChangeTracker.Entries<IExtensionOwnedEntity>().ToArray();

        foreach (var entry in entries.Where(entry => entry.State == EntityState.Added))
        {
            StampCreated(entry, userId, now);
        }

        foreach (
            var entry in entries.Where(entry =>
                entry.State != EntityState.Added && entry.State != EntityState.Unchanged
            )
        )
        {
            var databaseValues = entry.GetDatabaseValues();
            ValidatePersistedOwner(entry, databaseValues, userId);
            if (entry.State != EntityState.Deleted)
            {
                ValidateChildRoot(entry, databaseValues, userId);
            }
        }

        foreach (var entry in entries.Where(entry => entry.State == EntityState.Added))
        {
            ValidateChildRoot(entry, null, userId);
        }

        foreach (var entry in entries.Where(entry => entry.State == EntityState.Modified))
        {
            StampModified(entry, userId, now);
        }
    }

    private async Task ValidateAndStampOwnedEntitiesAsync(CancellationToken cancellationToken)
    {
        var userId = RequiredUserId();
        var now = _timeProvider.GetUtcNow();
        var entries = ChangeTracker.Entries<IExtensionOwnedEntity>().ToArray();

        foreach (var entry in entries.Where(entry => entry.State == EntityState.Added))
        {
            StampCreated(entry, userId, now);
        }

        foreach (
            var entry in entries.Where(entry =>
                entry.State != EntityState.Added && entry.State != EntityState.Unchanged
            )
        )
        {
            var databaseValues = await entry.GetDatabaseValuesAsync(cancellationToken);
            ValidatePersistedOwner(entry, databaseValues, userId);
            if (entry.State != EntityState.Deleted)
            {
                await ValidateChildRootAsync(entry, databaseValues, userId, cancellationToken);
            }
        }

        foreach (var entry in entries.Where(entry => entry.State == EntityState.Added))
        {
            await ValidateChildRootAsync(entry, null, userId, cancellationToken);
        }

        foreach (var entry in entries.Where(entry => entry.State == EntityState.Modified))
        {
            StampModified(entry, userId, now);
        }
    }

    private static void StampCreated(EntityEntry<IExtensionOwnedEntity> entry, string userId, DateTimeOffset now)
    {
        entry.Entity.CreateBy = userId;
        entry.Entity.CreateTime = now;
        entry.Entity.UpdateBy = null;
        entry.Entity.UpdateTime = null;
    }

    private static void StampModified(EntityEntry<IExtensionOwnedEntity> entry, string userId, DateTimeOffset now)
    {
        entry.Entity.UpdateBy = userId;
        entry.Entity.UpdateTime = now;
        entry.Property(entity => entity.UpdateBy).IsModified = true;
        entry.Property(entity => entity.UpdateTime).IsModified = true;
    }

    private static void ValidatePersistedOwner(
        EntityEntry<IExtensionOwnedEntity> entry,
        PropertyValues? databaseValues,
        string userId
    )
    {
        if (databaseValues is null)
        {
            throw InvalidOwnership();
        }

        var storedOwner = databaseValues[nameof(IExtensionOwnedEntity.CreateBy)] as string;
        var currentOwner = entry.Entity.CreateBy;
        var ownerProperty = entry.Property(entity => entity.CreateBy);
        if (
            !string.Equals(storedOwner, userId, StringComparison.Ordinal)
            || !string.Equals(currentOwner, storedOwner, StringComparison.Ordinal)
            || ownerProperty.IsModified && !Equals(ownerProperty.OriginalValue, ownerProperty.CurrentValue)
        )
        {
            throw InvalidOwnership();
        }
    }

    private void ValidateChildRoot(
        EntityEntry<IExtensionOwnedEntity> entry,
        PropertyValues? databaseValues,
        string userId
    )
    {
        var child = GetChildDescriptor(entry.Entity.GetType());
        if (child is null)
        {
            return;
        }

        var property = entry.Metadata.FindProperty(child.RootIdProperty.Name);
        if (property is null)
        {
            throw InvalidOwnership();
        }

        var currentRootId = property.PropertyInfo?.GetValue(entry.Entity);
        var rootId = databaseValues is null ? currentRootId : databaseValues[property.Name];
        if (databaseValues is not null && !Equals(currentRootId, rootId))
        {
            throw InvalidOwnership();
        }

        EnsureRootOwner(child.RootType, rootId, userId);
    }

    private async Task ValidateChildRootAsync(
        EntityEntry<IExtensionOwnedEntity> entry,
        PropertyValues? databaseValues,
        string userId,
        CancellationToken cancellationToken
    )
    {
        var child = GetChildDescriptor(entry.Entity.GetType());
        if (child is null)
        {
            return;
        }

        var property = entry.Metadata.FindProperty(child.RootIdProperty.Name);
        if (property is null)
        {
            throw InvalidOwnership();
        }

        var currentRootId = property.PropertyInfo?.GetValue(entry.Entity);
        var rootId = databaseValues is null ? currentRootId : databaseValues[property.Name];
        if (databaseValues is not null && !Equals(currentRootId, rootId))
        {
            throw InvalidOwnership();
        }

        await EnsureRootOwnerAsync(child.RootType, rootId, userId, cancellationToken);
    }

    private void EnsureRootOwner(Type rootType, object? rootId, string userId)
    {
        if (rootId is null || Find(rootType, [rootId]) is not IExtensionOwnedEntity root)
        {
            throw InvalidOwnership();
        }

        var rootEntry = Entry(root);
        if (rootEntry.State == EntityState.Added)
        {
            if (!string.Equals(root.CreateBy, userId, StringComparison.Ordinal))
            {
                throw InvalidOwnership();
            }

            return;
        }

        PropertyValues? databaseValues = rootEntry.State == EntityState.Deleted ? null : rootEntry.GetDatabaseValues();
        if (
            databaseValues is null
            || !string.Equals(
                databaseValues[nameof(IExtensionOwnedEntity.CreateBy)] as string,
                userId,
                StringComparison.Ordinal
            )
        )
        {
            throw InvalidOwnership();
        }
    }

    private async Task EnsureRootOwnerAsync(
        Type rootType,
        object? rootId,
        string userId,
        CancellationToken cancellationToken
    )
    {
        if (rootId is null || await FindAsync(rootType, [rootId], cancellationToken) is not IExtensionOwnedEntity root)
        {
            throw InvalidOwnership();
        }

        var rootEntry = Entry(root);
        if (rootEntry.State == EntityState.Added)
        {
            if (!string.Equals(root.CreateBy, userId, StringComparison.Ordinal))
            {
                throw InvalidOwnership();
            }

            return;
        }

        PropertyValues? databaseValues =
            rootEntry.State == EntityState.Deleted ? null : await rootEntry.GetDatabaseValuesAsync(cancellationToken);
        if (
            databaseValues is null
            || !string.Equals(
                databaseValues[nameof(IExtensionOwnedEntity.CreateBy)] as string,
                userId,
                StringComparison.Ordinal
            )
        )
        {
            throw InvalidOwnership();
        }
    }

    private static ExtensionChildDescriptor? GetChildDescriptor(Type entityType)
    {
        var childInterface = entityType
            .GetInterfaces()
            .FirstOrDefault(interfaceType =>
                interfaceType.IsGenericType
                && interfaceType.GetGenericTypeDefinition() == typeof(IExtensionOwnedChild<,>)
            );
        if (childInterface is null)
        {
            return null;
        }

        return new ExtensionChildDescriptor(
            childInterface.GetGenericArguments()[0],
            typeof(IExtensionOwnedChild<,>).GetProperty("RootId")!
        );
    }

    private string RequiredUserId()
    {
        var userId = _userContext.UserId;
        return string.IsNullOrWhiteSpace(userId) ? throw new AgwException(ErrorCodes.AuthenticationRequired) : userId;
    }

    private static AgwException InvalidOwnership() => new(ErrorCodes.InvalidParam);

    private sealed record ExtensionChildDescriptor(Type RootType, System.Reflection.PropertyInfo RootIdProperty);
}
