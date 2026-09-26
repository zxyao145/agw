using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using Agw.Auth.Contracts;
using Agw.Projects.Application.History;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.History;
using Agw.Projects.Domain.Behaviors;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Projects.Infrastructure;

/// <summary>
/// 对话历史表的读写：按消息身份更新或插入、分配序号、校验所属与 Generation，并提供一次执行的批量缓冲。
/// Reads and writes the conversation history table: upserts by message identity, assigns sequences, checks ownership and generation, and provides the batching buffer of one execution.
/// </summary>
public sealed partial class ConversationHistoryStore : IConversationHistoryStore
{
    private static readonly JsonSerializerOptions DefaultJsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        // 中文等非 ASCII 字符按原样写入 conversation_payload。
        // Write CJK and other non-ASCII characters as-is into conversation_payload.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static readonly Meter HistoryMeter = new("Agw.ConversationHistory.Persistence");
    private static readonly Histogram<double> FlushDuration = HistoryMeter.CreateHistogram<double>(
        "agw.history.flush.duration",
        "ms"
    );
    private static readonly Counter<long> WriteFailures = HistoryMeter.CreateCounter<long>(
        "agw.history.write.failures"
    );

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IApplicationLock _applicationLock;
    private readonly ILogger<ConversationHistoryStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly ConversationHistoryOptions _options;

    public ConversationHistoryStore(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<ConversationHistoryStore> logger,
        TimeProvider timeProvider,
        JsonSerializerOptions? jsonSerializerOptions = null
    )
        : this(serviceScopeFactory, InMemoryApplicationLock.Shared, logger, timeProvider, jsonSerializerOptions) { }

    public ConversationHistoryStore(
        IServiceScopeFactory serviceScopeFactory,
        IApplicationLock applicationLock,
        ILogger<ConversationHistoryStore> logger,
        TimeProvider timeProvider,
        JsonSerializerOptions? jsonSerializerOptions = null,
        IOptions<ConversationHistoryOptions>? options = null
    )
    {
        _options = options?.Value ?? new ConversationHistoryOptions();
        _serviceScopeFactory = serviceScopeFactory;
        _applicationLock = applicationLock;
        _logger = logger;
        _timeProvider = timeProvider;
        _jsonSerializerOptions = jsonSerializerOptions ?? DefaultJsonSerializerOptions;
    }

    public IConversationHistoryBuffer BeginBuffer(
        ConversationHistoryScope scope,
        CancellationToken ownershipLost = default,
        IExecutionWriteGuard? writeGuard = null
    )
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new HistoryBuffer(this, Normalize(scope), ownershipLost, writeGuard);
    }

    public async Task<IReadOnlyList<ConversationHistoryEntry>> ReadAsync(
        ConversationHistoryScope scope,
        string? historyScope,
        IConversationHistoryBuffer? buffer,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope = Normalize(scope);
        var pending = Applicable(buffer, scope);
        if (pending != null)
            await pending.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            pending?.EnsureActive();
            await using var serviceScope = _serviceScopeFactory.CreateAsyncScope();
            var db = serviceScope.ServiceProvider.GetRequiredService<IProjectsDbContext>();
            var conversation = await db
                .ProjectConversations.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectId == scope.ProjectId && item.ContextId == scope.ContextId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (pending != null && conversation != null && conversation.Generation != pending.Scope.Generation)
                throw new AgwException(ErrorCodes.ConversationSessionConflict);
            if (pending != null && conversation == null && pending.IsExecutionBound)
                throw new AgwException(ErrorCodes.ConversationSessionConflict);
            // 模型历史只需要行 Id、序号、载荷与创建时间；投影查询不加载 Metadata，也不物化实体。
            // Model history needs only the row Id, sequence, payload and creation time; the projected query loads no Metadata and materializes no entity.
            var rows =
                conversation == null
                    ? []
                    : await db
                        .ProjectConversationChatHistories.AsNoTracking()
                        .Where(record =>
                            record.ConversationId == conversation.Id
                            && record.ConversationPayload != null
                            && record.HistoryScope == historyScope
                        )
                        .Select(record => new HistoryRow
                        {
                            Id = record.Id,
                            Sequence = record.ConversationSequence,
                            Payload = record.ConversationPayload!,
                            CreateTime = record.CreateTime,
                        })
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
            if (pending != null)
            {
                var known = rows.ToDictionary(row => row.Id);
                // 未提交记录的临时序号只用于排序：接在已提交的最大序号之后。
                // Temporary sequences of uncommitted records only order them after the largest committed sequence.
                var sequence =
                    conversation == null
                        ? -1
                        : await db
                            .ProjectConversationChatHistories.Where(record => record.ConversationId == conversation.Id)
                            .MaxAsync(record => record.ConversationSequence, cancellationToken)
                            .ConfigureAwait(false)
                            ?? -1;
                foreach (var record in pending.Snapshot())
                {
                    if (known.TryGetValue(record.Id, out var existing))
                        existing.Payload = record.Payload;
                    else if (string.Equals(record.HistoryScope, historyScope, StringComparison.Ordinal))
                        rows.Add(
                            new HistoryRow
                            {
                                Id = record.Id,
                                Sequence = ++sequence,
                                Payload = record.Payload,
                                CreateTime = record.CreatedAt,
                            }
                        );
                }
            }

            return rows.OrderBy(row => row.Sequence ?? long.MinValue)
                .ThenBy(row => row.CreateTime)
                .ThenBy(row => row.Id)
                .Select(row => new ConversationHistoryEntry(row.Id, row.Sequence, row.Payload, null, row.CreateTime))
                .ToList();
        }
        finally
        {
            pending?.Gate.Release();
        }
    }

    public Task UpsertAsync(
        ConversationMessageWriteScope scope,
        IReadOnlyList<ConversationMessageSnapshot> snapshots,
        CancellationToken cancellationToken
    ) =>
        AppendRecordsAsync(
            scope.ProjectId,
            ContextIdUtil.NormalizeContextId(scope.ContextId),
            snapshots.Select(snapshot => CreateSnapshotRecord(scope, snapshot)).ToArray(),
            scope.Generation,
            scope.IsExecutionBound,
            writeGuard: null,
            cancellationToken
        );

    /// <summary>
    /// 在项目生命周期锁与对话写入锁内提交记录：新消息按顺序分配序号，已有消息由同一生产者更新。返回提交后对话的最大序号；没有记录或项目不属于当前用户时为空。
    /// 给出写入入口时，提交在它的租约检查事务中完成。
    /// Commits records under the project lifecycle and conversation write locks: new messages receive ordered sequences and existing ones are updated by their producer. Returns the largest sequence of the conversation after the commit; null when there are no records or the project belongs to another user.
    /// With a write guard, the commit runs inside its lease-checked transaction.
    /// </summary>
    private async Task<long?> AppendRecordsAsync(
        Guid projectId,
        string contextId,
        IReadOnlyList<PendingHistoryRecord> records,
        int expectedGeneration,
        bool isExecutionBound,
        IExecutionWriteGuard? writeGuard,
        CancellationToken cancellationToken
    )
    {
        if (records.Count == 0)
            return null;
        await using var lifecycleLease = await _applicationLock
            .AcquireAsync(ProjectLifecycleLock.GetResourceName(projectId), cancellationToken)
            .ConfigureAwait(false);
        await using var mutationLease = await _applicationLock
            .AcquireAsync(ConversationHistoryLock.GetResourceName(projectId, contextId), cancellationToken)
            .ConfigureAwait(false);
        if (writeGuard != null)
            return await writeGuard
                .RunAsync(
                    (services, token) =>
                        AppendRecordsCoreAsync(
                            services.GetRequiredService<IProjectsDbContext>(),
                            projectId,
                            contextId,
                            records,
                            expectedGeneration,
                            isExecutionBound,
                            token
                        ),
                    cancellationToken
                )
                .ConfigureAwait(false);

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        return await AppendRecordsCoreAsync(
                scope.ServiceProvider.GetRequiredService<IProjectsDbContext>(),
                projectId,
                contextId,
                records,
                expectedGeneration,
                isExecutionBound,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<long?> AppendRecordsCoreAsync(
        IProjectsDbContext dbContext,
        Guid projectId,
        string contextId,
        IReadOnlyList<PendingHistoryRecord> records,
        int expectedGeneration,
        bool isExecutionBound,
        CancellationToken cancellationToken
    )
    {
        if (!UserInfoUtil.IsContextActive)
        {
            throw new AgwException(ErrorCodes.AuthenticationRequired);
        }

        var ownerUserId = UserInfoUtil.RequiredUserId;
        if (
            !await dbContext
                .Projects.AnyAsync(
                    project => project.Id == projectId && project.CreateBy == ownerUserId,
                    cancellationToken
                )
                .ConfigureAwait(false)
        )
        {
            if (isExecutionBound)
                throw new AgwException(ErrorCodes.ConversationSessionConflict);
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var firstUserText = records
            .Select(record => record.UserText)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        var projectConversation = await dbContext
            .ProjectConversations.SingleOrDefaultAsync(
                x => x.ProjectId == projectId && x.ContextId == contextId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (projectConversation == null)
        {
            if (isExecutionBound)
                throw new AgwException(ErrorCodes.ResourceNotFound);
            projectConversation = new ProjectConversation
            {
                Id = Guid.CreateVersion7(),
                ProjectId = projectId,
                ContextId = contextId,
                CreateBy = ownerUserId,
                CreateTime = now,
                UpdateBy = ownerUserId,
                UpdateTime = now,
            };
            new ProjectConversationBehavior(projectConversation).SetInitialTitle(null, firstUserText);
            dbContext.ProjectConversations.Add(projectConversation);
        }
        else
        {
            new ProjectConversationBehavior(projectConversation).TryAdoptDerivedTitle(firstUserText);
        }

        if (projectConversation.Generation != expectedGeneration)
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        var ids = records.Select(record => record.Id).ToArray();
        var existingRows = await dbContext
            .ProjectConversationChatHistories.Where(record =>
                record.ConversationId == projectConversation.Id && ids.Contains(record.Id)
            )
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var existingById = existingRows.ToDictionary(record => record.Id);
        var pending = records.Where(record => !existingById.ContainsKey(record.Id)).ToList();
        var updated = false;
        foreach (var record in records)
        {
            if (!existingById.TryGetValue(record.Id, out var existing))
                continue;
            if (existing.TaskId != record.ProducerId)
            {
                // Another producer owns this row. Leave it alone, but keep writing the rest of the
                // batch: one fenced message must not discard every other pending message.
                _logger.LogWarning(
                    "Skipped history message {MessageId} written by producer {Producer}; the row belongs to {Owner}.",
                    record.Id,
                    record.ProducerId,
                    existing.TaskId
                );
                continue;
            }
            if (existing.ConversationPayload == record.Payload)
                continue;
            existing.ConversationPayload = record.Payload;
            existing.Metadata = record.Metadata;
            existing.AgentName = record.AgentName;
            existing.UpdateTime = now;
            updated = true;
        }
        var nextSequence =
            await dbContext
                .ProjectConversationChatHistories.Where(x => x.ConversationId == projectConversation.Id)
                .Select(x => x.ConversationSequence)
                .MaxAsync(cancellationToken)
                .ConfigureAwait(false)
            ?? -1;
        // 重试时记录已经全部提交（上次提交成功但确认丢失），返回已提交的最大序号。
        // On a retry every record is already committed (the earlier commit succeeded but its acknowledgement was lost), so the committed maximum is returned.
        if (pending.Count == 0 && !updated)
            return nextSequence;

        foreach (var record in pending)
        {
            nextSequence++;
            var entity = ToEntity(record, projectConversation.Id, nextSequence);
            entity.UpdateTime = now;
            dbContext.ProjectConversationChatHistories.Add(entity);
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await dbContext
                .SaveConversationChangesAsync(projectConversation.Id, expectedGeneration, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            WriteFailures.Add(1);
            throw;
        }
        finally
        {
            FlushDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        return nextSequence;
    }

    /// <summary>
    /// 把投影快照转换为待写记录：校验消息身份，合并展示元数据，并用本存储的序列化选项写出消息。每个快照只序列化这一次。
    /// Converts a projection snapshot into a pending record: validates the message identity, merges display metadata and writes the message with this store's serializer options. Each snapshot is serialized only here.
    /// </summary>
    private PendingHistoryRecord CreateSnapshotRecord(
        ConversationMessageWriteScope scope,
        ConversationMessageSnapshot snapshot
    )
    {
        if (scope.ProducerId == Guid.Empty || snapshot.MessageId == Guid.Empty)
            throw new AgwException(ErrorCodes.InvalidParam);
        if (!Guid.TryParse(snapshot.Message.MessageId, out var messageId) || messageId != snapshot.MessageId)
            throw new AgwException(ErrorCodes.InvalidParam);
        // 时间戳以 DateTimeOffset 值写出，负载中保持 RFC 3339 原文；在浅复制上补齐，捕获的消息保持不变。
        // The timestamp is written as a DateTimeOffset value, keeping its RFC 3339 text in the payload; it is filled on a shallow copy so the captured message stays unchanged.
        var message = MessageTimestampMetadata.EnsureCreatedAt(snapshot.Message.Clone(), snapshot.CreatedAt);
        var metadata = ProjectConversationChatHistoryMetadataFactory.FromMessage(message) ?? [];
        foreach (var (key, value) in snapshot.Metadata)
            metadata[key] = value;
        metadata["producerScopeId"] = JsonSerializer.SerializeToElement(scope.ProducerId);
        metadata["generation"] = JsonSerializer.SerializeToElement(scope.Generation);
        return new PendingHistoryRecord(
            snapshot.MessageId,
            scope.ProducerId,
            snapshot.CreatedAt,
            snapshot.Author,
            message.Role == ChatRole.User ? ExtractText(message) : null,
            JsonSerializer.Serialize(message, _jsonSerializerOptions),
            metadata
        )
        {
            TurnId = scope.TurnId,
            AgentId = scope.AgentId,
            HistoryScope = scope.HistoryScope,
            StepIndex = snapshot.StepIndex,
            Purpose =
                snapshot.IsResult || AgwMessageClassifier.IsResult(message)
                    ? ConversationMessagePurpose.Result
                    : ConversationMessagePurpose.Message,
        };
    }

    private static ConversationHistoryScope Normalize(ConversationHistoryScope scope) =>
        scope with
        {
            ContextId = ContextIdUtil.NormalizeContextId(scope.ContextId),
        };

    /// <summary>
    /// 缓冲属于同一对话时参与本次读取；同一对话的 Generation 或所属用户不同即冲突。
    /// A buffer of the same conversation joins this read; a different generation or owner on the same conversation conflicts.
    /// </summary>
    private HistoryBuffer? Applicable(IConversationHistoryBuffer? buffer, ConversationHistoryScope scope)
    {
        if (
            buffer is not HistoryBuffer pending
            || !ReferenceEquals(pending.Store, this)
            || pending.Scope.ProjectId != scope.ProjectId
            || !string.Equals(pending.Scope.ContextId, scope.ContextId, StringComparison.Ordinal)
        )
            return null;
        if (pending.Scope.Generation != scope.Generation || pending.Owner != UserInfoUtil.RequiredUserId)
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        return pending;
    }

    private static string ExtractText(ChatMessage message) =>
        string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text)).Trim();

    private static ProjectConversationChatHistory ToEntity(
        PendingHistoryRecord record,
        Guid conversationId,
        long sequence
    ) =>
        new()
        {
            Id = record.Id,
            ConversationId = conversationId,
            TaskId = record.ProducerId,
            Status = TaskExecutionStatus.Succeeded,
            AgentName = record.AgentName,
            ConversationSequence = sequence,
            ConversationPayload = record.Payload,
            Metadata = record.Metadata,
            TurnId = record.TurnId,
            StepIndex = record.StepIndex,
            AgentId = record.AgentId,
            HistoryScope = record.HistoryScope,
            Purpose = record.Purpose,
            CreateTime = record.CreatedAt,
            UpdateTime = record.CreatedAt,
        };

    /// <summary>
    /// 模型历史读取的一行：只含排序与模型输入需要的列，合并待写快照时替换载荷。
    /// One row of the model history read: only the columns needed for ordering and model input; merging pending snapshots replaces its payload.
    /// </summary>
    private sealed class HistoryRow
    {
        public required Guid Id { get; init; }
        public required long? Sequence { get; init; }
        public required string Payload { get; set; }
        public required DateTimeOffset CreateTime { get; init; }
    }

    /// <summary>
    /// 待写的一行：CreatedAt 是消息在投影中第一次出现的时间，作为行的创建时间；归属列只在插入时写入。
    /// One pending row: CreatedAt is when the message first appeared in the projection and becomes the row's creation time; the ownership columns are written at insert only.
    /// </summary>
    private sealed record PendingHistoryRecord(
        Guid Id,
        Guid ProducerId,
        DateTimeOffset CreatedAt,
        string? AgentName,
        string? UserText,
        string Payload,
        Dictionary<string, JsonElement> Metadata
    )
    {
        public Guid? TurnId { get; init; }
        public Guid? AgentId { get; init; }
        public string? HistoryScope { get; init; }
        public int? StepIndex { get; init; }
        public ConversationMessagePurpose Purpose { get; init; }
    }
}
