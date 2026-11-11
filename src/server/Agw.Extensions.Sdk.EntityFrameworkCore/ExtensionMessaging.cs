using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

public static class ExtensionOutbox
{
    public static Guid Enqueue<TPayload>(
        ExtensionDbContext dbContext,
        string kind,
        string idempotencyKey,
        TPayload payload,
        JsonSerializerOptions jsonOptions
    )
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        if (!ExtensionOutboxKind.IsDefined(kind) || string.IsNullOrWhiteSpace(dbContext.CurrentUserId))
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        Guid id = Guid.CreateVersion7();
        dbContext.Add(
            new ExtensionOutboxRow
            {
                Id = id,
                Kind = kind,
                IdempotencyKey = idempotencyKey,
                PayloadJson = JsonSerializer.Serialize(payload, jsonOptions),
                State = ExtensionOutboxState.Pending,
            }
        );
        return id;
    }

    public static async Task<bool> CancelPendingAsync(
        ExtensionDbContext dbContext,
        Guid outboxId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        if (string.IsNullOrWhiteSpace(dbContext.CurrentUserId))
            throw new AgwException(ErrorCodes.AuthenticationRequired);
        var row =
            dbContext
                .Set<ExtensionOutboxRow>()
                .Local.FirstOrDefault(item =>
                    item.Id == outboxId
                    && (
                        item.CreateBy == dbContext.CurrentUserId
                        || item.CreateBy is null && dbContext.Entry(item).State == EntityState.Added
                    )
                )
            ?? await dbContext
                .Set<ExtensionOutboxRow>()
                .SingleOrDefaultAsync(item => item.Id == outboxId, cancellationToken);
        if (row is null || row.State != ExtensionOutboxState.Pending || row.Kind != ExtensionOutboxKind.SubmitExecution)
            return false;
        row.State = ExtensionOutboxState.Completed;
        row.CompletedAt = dbContext.GetUtcNow();
        row.NextAttemptAt = null;
        return true;
    }
}

public static class ExtensionMessagingModelBuilderExtensions
{
    public static ModelBuilder UseExtensionMessaging(
        this ModelBuilder modelBuilder,
        string tablePrefix = "",
        string? dateTimeOffsetType = null
    )
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ExtensionInboxRow>(builder => ConfigureInbox(builder, tablePrefix, dateTimeOffsetType));
        modelBuilder.Entity<ExtensionOutboxRow>(builder => ConfigureOutbox(builder, tablePrefix, dateTimeOffsetType));
        return modelBuilder;
    }

    private static void ConfigureInbox(
        EntityTypeBuilder<ExtensionInboxRow> builder,
        string tablePrefix,
        string? dateTimeOffsetType
    )
    {
        string tableName = $"{tablePrefix}__inbox";
        builder.ToTable(tableName);
        builder.HasKey(row => row.DeliveryId).HasName($"pk_{tableName}");
        builder.Property(row => row.DeliveryId).HasColumnName("delivery_id").ValueGeneratedNever();
        builder.Property(row => row.CreateBy).HasColumnName("create_by").IsRequired();
        ConfigureTimestamp(builder.Property(row => row.CreateTime).HasColumnName("create_time"), dateTimeOffsetType);
        builder.Property(row => row.UpdateBy).HasColumnName("update_by");
        ConfigureTimestamp(builder.Property(row => row.UpdateTime).HasColumnName("update_time"), dateTimeOffsetType);
        var processedAt = builder.Property(row => row.ProcessedAt).HasColumnName("processed_at").IsRequired();
        ConfigureTimestamp(processedAt, dateTimeOffsetType);
        builder.Property(row => row.ResultJson).HasColumnName("result_json").HasColumnType("text").IsRequired();
    }

    private static void ConfigureOutbox(
        EntityTypeBuilder<ExtensionOutboxRow> builder,
        string tablePrefix,
        string? dateTimeOffsetType
    )
    {
        string tableName = $"{tablePrefix}__outbox";
        builder.ToTable(tableName);
        builder.HasKey(row => row.Id).HasName($"pk_{tableName}");
        builder.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(row => row.CreateBy).HasColumnName("create_by").IsRequired();
        var createTime = builder.Property(row => row.CreateTime).HasColumnName("create_time").IsRequired();
        var updateTime = builder.Property(row => row.UpdateTime).HasColumnName("update_time");
        var nextAttemptAt = builder.Property(row => row.NextAttemptAt).HasColumnName("next_attempt_at");
        var completedAt = builder.Property(row => row.CompletedAt).HasColumnName("completed_at");
        ConfigureTimestamp(createTime, dateTimeOffsetType);
        ConfigureTimestamp(updateTime, dateTimeOffsetType);
        ConfigureTimestamp(nextAttemptAt, dateTimeOffsetType);
        ConfigureTimestamp(completedAt, dateTimeOffsetType);
        builder.Property(row => row.UpdateBy).HasColumnName("update_by");
        builder.Property(row => row.Kind).HasColumnName("kind").HasColumnType("text").IsRequired();
        builder.Property(row => row.IdempotencyKey).HasColumnName("idempotency_key").HasColumnType("text").IsRequired();
        builder.HasIndex(row => row.IdempotencyKey).HasDatabaseName($"{tablePrefix}uq_outbox_key").IsUnique();
        builder.Property(row => row.PayloadJson).HasColumnName("payload_json").HasColumnType("text").IsRequired();
        builder
            .Property(row => row.State)
            .HasColumnName("state")
            .HasConversion<string>()
            .HasColumnType("text")
            .IsRequired();
        builder.Property(row => row.ResultJson).HasColumnName("result_json").HasColumnType("text");
        builder.Property(row => row.ErrorCode).HasColumnName("error_code");
        builder.Property(row => row.Attempt).HasColumnName("attempt").IsRequired();
        builder.Property(row => row.LastError).HasColumnName("last_error").HasColumnType("text");
    }

    private static void ConfigureTimestamp(PropertyBuilder property, string? columnType)
    {
        if (columnType == "TEXT")
            property
                .HasConversion(
                    new ValueConverter<DateTimeOffset, long>(
                        value => value.UtcTicks,
                        value => new DateTimeOffset(value, TimeSpan.Zero)
                    )
                )
                .HasColumnType("INTEGER");
        else if (columnType is not null)
            property.HasColumnType(columnType);
    }
}

internal sealed class ExtensionInboxRow : IExtensionOwnedEntity
{
    public Guid DeliveryId { get; set; }

    public string? CreateBy { get; set; }

    public DateTimeOffset CreateTime { get; set; }

    public string? UpdateBy { get; set; }

    public DateTimeOffset? UpdateTime { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }

    public string ResultJson { get; set; } = string.Empty;
}

internal sealed class ExtensionOutboxRow : IExtensionOwnedEntity
{
    public Guid Id { get; set; }

    public string? CreateBy { get; set; }

    public DateTimeOffset CreateTime { get; set; }

    public string? UpdateBy { get; set; }

    public DateTimeOffset? UpdateTime { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public ExtensionOutboxState State { get; set; }

    public string? ResultJson { get; set; }

    public int? ErrorCode { get; set; }

    public int Attempt { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? LastError { get; set; }
}

public abstract class ExtensionEventHandlerBase<TDbContext>
    where TDbContext : ExtensionDbContext
{
    public async ValueTask<ExtensionEventHandlerResult> ProcessAsync(
        TDbContext dbContext,
        IExtensionContext context,
        ExtensionEventDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.DeliveryId == Guid.Empty || dbContext.CurrentUserId is null)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        ExtensionInboxRow? existing = await dbContext
            .Set<ExtensionInboxRow>()
            .SingleOrDefaultAsync(row => row.DeliveryId == delivery.DeliveryId, cancellationToken);
        if (existing is not null)
        {
            ExtensionEventHandlerResult storedResult =
                JsonSerializer.Deserialize<ExtensionEventHandlerResult>(existing.ResultJson, context.JsonOptions)
                ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
            await transaction.CommitAsync(cancellationToken);
            return storedResult;
        }

        ExtensionEventHandlerResult result = await HandleEventAsync(dbContext, context, delivery, cancellationToken);
        ValidateResult(result);
        if (result.Kind == ExtensionEventHandlerResultKind.Retry)
        {
            await transaction.RollbackAsync(cancellationToken);
            return result;
        }

        dbContext.Add(
            new ExtensionInboxRow
            {
                DeliveryId = delivery.DeliveryId,
                ProcessedAt = dbContext.GetUtcNow(),
                ResultJson = JsonSerializer.Serialize(result, context.JsonOptions),
            }
        );
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    protected abstract ValueTask<ExtensionEventHandlerResult> HandleEventAsync(
        TDbContext dbContext,
        IExtensionContext context,
        ExtensionEventDelivery delivery,
        CancellationToken cancellationToken
    );

    private static void ValidateResult(ExtensionEventHandlerResult result)
    {
        if (result is null || !Enum.IsDefined(result.Kind))
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }

        if (
            result.Kind == ExtensionEventHandlerResultKind.Retry
            && (
                result.RetryAfter is null
                || result.RetryAfter <= TimeSpan.Zero
                || string.IsNullOrWhiteSpace(result.Reason)
            )
        )
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }

        if (result.Kind == ExtensionEventHandlerResultKind.Failed && string.IsNullOrWhiteSpace(result.Reason))
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }
    }
}
