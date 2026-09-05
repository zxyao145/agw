using Agw.Agents.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agw.Infrastructure.Agents;

public sealed class DurableExecutionScopeMaintenance : IDurableExecutionScopeMaintenance
{
    private const int BatchSize = 128;
    private const int MaxBatchesPerPass = 4;
    private static readonly TimeSpan PassBudget = TimeSpan.FromSeconds(1);
    private readonly AgwDbContext _dbContext;
    private readonly IApplicationLock _applicationLock;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DurableExecutionScopeMaintenance> _logger;

    public DurableExecutionScopeMaintenance(
        AgwDbContext dbContext,
        IApplicationLock applicationLock,
        TimeProvider timeProvider,
        ILogger<DurableExecutionScopeMaintenance> logger
    )
    {
        _dbContext = dbContext;
        _applicationLock = applicationLock;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<DurableExecutionScopeBackfillResult> BackfillAsync(
        CancellationToken cancellationToken = default,
        DurableExecutionScopeCursor? after = null
    )
    {
        var cursor = after;
        var startedAt = _timeProvider.GetTimestamp();
        var visited = false;
        for (var batch = 0; batch < MaxBatchesPerPass; batch++)
        {
            var pending = _dbContext.DurableExecutions.AsNoTracking().Where(item => !item.ScopeBackfilled);
            if (cursor != null)
            {
                var userId = cursor.UserId;
                var id = cursor.Id;
                pending = pending.Where(item =>
                    string.Compare(item.UserId, userId) > 0 || item.UserId == userId && item.Id.CompareTo(id) > 0
                );
            }
            var candidates = await pending
                .OrderBy(item => item.UserId)
                .ThenBy(item => item.Id)
                .Select(item => new DurableExecutionScopeCursor(item.UserId, item.Id))
                .Take(BatchSize)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            if (candidates.Length == 0)
            {
                cursor = null;
                break;
            }
            var ids = candidates.Select(item => item.Id).ToArray();
            var rows = await ProjectManifests(
                    _dbContext
                        .DurableExecutions.AsNoTracking()
                        .Where(item => ids.Contains(item.Id) && !item.ScopeBackfilled)
                )
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var byId = rows.ToDictionary(row => row.Id);
            foreach (var candidate in candidates)
            {
                if (visited && _timeProvider.GetElapsedTime(startedAt) >= PassBudget)
                {
                    return await GetBackfillResultAsync(cursor, cancellationToken).ConfigureAwait(false);
                }
                visited = true;
                cursor = candidate;
                if (!byId.TryGetValue(candidate.Id, out var row))
                {
                    continue;
                }
                var id = row.Id;
                var scope = ReadScope(row);
                if (scope != null)
                {
                    await TryStampAsync(row, scope, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // An executing worker owns this lock; never quarantine an in-flight execution underneath it.
                await using var lease = await TryAcquireAsync(id, cancellationToken).ConfigureAwait(false);
                if (lease == null)
                {
                    continue;
                }
                await ValidateLockedExecutionAsync(id, cancellationToken).ConfigureAwait(false);
            }
            // Advance past deferred rows too. Never re-read a busy head within this sweep.
            if (candidates.Length < BatchSize)
            {
                cursor = null;
                break;
            }
        }
        return await GetBackfillResultAsync(cursor, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DurableExecutionScopeBackfillResult> GetBackfillResultAsync(
        DurableExecutionScopeCursor? cursor,
        CancellationToken cancellationToken
    )
    {
        var hasPending = await _dbContext
            .DurableExecutions.AnyAsync(item => !item.ScopeBackfilled, cancellationToken)
            .ConfigureAwait(false);
        return new DurableExecutionScopeBackfillResult(hasPending ? cursor : null, hasPending);
    }

    public async Task<bool> RepairAndCheckActiveExecutionsAsync(
        Guid projectId,
        Guid conversationId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    )
    {
        var ids = await ActiveExecutions(projectId, conversationId, ownerUserId)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var id in ids)
        {
            await using var lease = await TryAcquireAsync(id, cancellationToken).ConfigureAwait(false);
            if (lease == null)
            {
                return true;
            }
            await ValidateLockedExecutionAsync(id, cancellationToken).ConfigureAwait(false);
        }
        return await ActiveExecutions(projectId, conversationId, ownerUserId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ValidateLockedExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken = default
    )
    {
        var row = await ReadAsync(executionId, cancellationToken).ConfigureAwait(false);
        if (row == null)
        {
            return false;
        }
        var scope = ReadScope(row);
        return await ValidateAsync(row, scope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DurableExecutionRecord?> LoadValidatedExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken = default
    )
    {
        DurableExecutionRecord? record;
        try
        {
            // Healthy segment starts load and decrypt exactly once; the store reuses this record.
            record = await _dbContext
                .DurableExecutions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == executionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AgwException exception) when (exception.Code == ErrorCodes.EncryptedDataInvalid.Code)
        {
            // Materialization can fail before an entity exists. Read scalar metadata for safe CAS quarantine.
            var damaged = await ReadAsync(executionId, cancellationToken).ConfigureAwait(false);
            if (damaged != null)
            {
                var scope = ReadScope(damaged);
                await QuarantineAsync(damaged, scope, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }
        if (record == null)
        {
            return null;
        }
        var parsedScope = DurableExecutionManifestScopeReader.Read(record.ManifestJson, record.Id, record.UserId);
        var row = new StoredManifest(
            record.Id,
            record.UserId,
            record.ManifestJson,
            record.StateVersion,
            record.Status,
            record.ProjectId,
            record.ProjectConversationId,
            record.ScopeBackfilled,
            record.StateChangedAt
        );
        if (!await ValidateAsync(row, parsedScope, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        record.ProjectId = parsedScope!.ProjectId;
        record.ProjectConversationId = parsedScope.ProjectConversationId;
        record.ScopeBackfilled = true;
        return record;
    }

    private async Task<bool> ValidateAsync(
        StoredManifest row,
        DurableExecutionScope? scope,
        CancellationToken cancellationToken
    )
    {
        if (
            scope != null
            && (
                !row.ScopeBackfilled
                || row.ProjectId == scope.ProjectId && row.ProjectConversationId == scope.ProjectConversationId
            )
        )
        {
            if (!row.ScopeBackfilled)
            {
                return await TryStampAsync(row, scope, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
        await QuarantineAsync(row, scope, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task<bool> TryStampAsync(
        StoredManifest row,
        DurableExecutionScope scope,
        CancellationToken cancellationToken
    )
    {
        // Derived indexing is not a business transition: preserve audit timestamps and StateVersion.
        // Zero rows means another writer won. Leave it for a fresh read, never fail the entire batch.
        return await _dbContext
                .DurableExecutions.Where(item =>
                    item.Id == row.Id && item.StateVersion == row.StateVersion && !item.ScopeBackfilled
                )
                .ExecuteUpdateAsync(
                    setters =>
                        setters
                            .SetProperty(item => item.ProjectId, scope.ProjectId)
                            .SetProperty(item => item.ProjectConversationId, scope.ProjectConversationId)
                            .SetProperty(item => item.ScopeBackfilled, true),
                    cancellationToken
                )
                .ConfigureAwait(false) > 0;
    }

    private async Task QuarantineAsync(
        StoredManifest row,
        DurableExecutionScope? scope,
        CancellationToken cancellationToken
    )
    {
        var active = !DurableExecutionQueries.IsTerminal(row.Status);
        // Preserve a trusted index when ciphertext is unreadable; invalidate an explicitly contradicted index.
        var discardScope =
            !row.ScopeBackfilled
            || scope != null
                && (row.ProjectId != scope.ProjectId || row.ProjectConversationId != scope.ProjectConversationId);
        Guid? projectId = discardScope ? null : row.ProjectId;
        Guid? conversationId = discardScope ? null : row.ProjectConversationId;
        var now = _timeProvider.GetUtcNow();
        var changed = await _dbContext
            .DurableExecutions.Where(item => item.Id == row.Id && item.StateVersion == row.StateVersion)
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(item => item.ScopeBackfilled, true)
                        .SetProperty(item => item.ProjectId, projectId)
                        .SetProperty(item => item.ProjectConversationId, conversationId)
                        .SetProperty(item => item.Status, active ? DurableExecutionStatus.Failed : row.Status)
                        .SetProperty(item => item.StateVersion, Guid.CreateVersion7())
                        .SetProperty(item => item.StateChangedAt, active ? now : row.StateChangedAt)
                        .SetProperty(item => item.UpdateTime, now)
                        .SetProperty(item => item.UpdateBy, row.UserId),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (changed == 0)
        {
            return; // In particular, a concurrent Interrupt must win without an error or state overwrite.
        }
        _logger.LogWarning(
            "Quarantined durable execution {ExecutionId}: invalid manifest or inconsistent scope. Payload retained for recovery.",
            row.Id
        );
    }

    private IQueryable<DurableExecutionRecord> ActiveExecutions(
        Guid projectId,
        Guid conversationId,
        string ownerUserId
    ) =>
        _dbContext
            .DurableExecutions.AsNoTracking()
            .InConversation(projectId, conversationId, ownerUserId)
            .Where(DurableExecutionQueries.Active);

    private Task<StoredManifest?> ReadAsync(Guid executionId, CancellationToken cancellationToken) =>
        ProjectManifests(_dbContext.DurableExecutions.AsNoTracking().Where(item => item.Id == executionId))
            .SingleOrDefaultAsync(cancellationToken);

    private static IQueryable<StoredManifest> ProjectManifests(IQueryable<DurableExecutionRecord> query) =>
        query.Select(item => new StoredManifest(
            item.Id,
            item.UserId,
            item.ManifestJson,
            item.StateVersion,
            item.Status,
            item.ProjectId,
            item.ProjectConversationId,
            item.ScopeBackfilled,
            item.StateChangedAt
        ));

    private DurableExecutionScope? ReadScope(StoredManifest row)
    {
        // Scalar projection intentionally avoids decrypting unrelated checkpoint/response fields.
        // Explicitly use the configured entity decryptor for the manifest alone.
        var entity = new DurableExecutionRecord { Id = row.Id, ManifestJson = row.ProtectedManifest };
        try
        {
            _dbContext.DecryptMaterializedEntity(entity);
            return DurableExecutionManifestScopeReader.Read(entity.ManifestJson, row.Id, row.UserId);
        }
        catch (AgwException exception) when (exception.Code == ErrorCodes.EncryptedDataInvalid.Code)
        {
            return null;
        }
    }

    private async Task<IAsyncDisposable?> TryAcquireAsync(Guid executionId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(250));
        try
        {
            return await _applicationLock
                .AcquireAsync(DurableExecutionLock.GetResourceName(executionId), timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private sealed record StoredManifest(
        Guid Id,
        string UserId,
        string ProtectedManifest,
        Guid StateVersion,
        DurableExecutionStatus Status,
        Guid? ProjectId,
        Guid? ProjectConversationId,
        bool ScopeBackfilled,
        DateTimeOffset StateChangedAt
    );
}
