using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

public sealed class ExtensionOwnedRecordScheduler<TDbContext, TRecord>
    where TDbContext : ExtensionDbContext
    where TRecord : class, IExtensionOwnedEntity
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IExtensionBackgroundScheduler _backgroundScheduler;
    private readonly IExtensionUserBackgroundScheduler _userScheduler;

    public ExtensionOwnedRecordScheduler(IServiceScopeFactory scopeFactory, IExtensionBackgroundScheduler scheduler)
    {
        _scopeFactory = scopeFactory;
        _backgroundScheduler = scheduler;
        _userScheduler =
            scheduler as IExtensionUserBackgroundScheduler ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
    }

    public async Task<IReadOnlyList<Guid>> ListIdsAsync(CancellationToken cancellationToken = default)
    {
        string? currentUserId = await GetCurrentUserIdAsync();
        return currentUserId is null
            ? await _backgroundScheduler.ExecuteAsync(
                async (_, token) => await ReadRecordIdsAsync(token),
                cancellationToken
            )
            : await _userScheduler.ExecuteAsync(
                currentUserId,
                Guid.CreateVersion7(),
                async (_, token) => await ReadRecordIdsAsync(token),
                cancellationToken
            );
    }

    public async ValueTask<TResult> ExecuteAsync<TResult>(
        Guid recordId,
        Func<TDbContext, TRecord, IExtensionContext, CancellationToken, ValueTask<TResult>> work,
        CancellationToken cancellationToken = default
    )
    {
        string? currentUserId = await GetCurrentUserIdAsync();
        ExtensionOwnedRecordIdentity? identity = currentUserId is null
            ? await _backgroundScheduler.ExecuteAsync(
                async (_, token) => await ReadRecordIdentityAsync(recordId, token),
                cancellationToken
            )
            : await _userScheduler.ExecuteAsync(
                currentUserId,
                Guid.CreateVersion7(),
                async (_, token) => await ReadRecordIdentityAsync(recordId, token),
                cancellationToken
            );
        if (identity is null)
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        if (string.IsNullOrWhiteSpace(identity.UserId))
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        return await _userScheduler.ExecuteAsync(
            identity.UserId,
            Guid.NewGuid(),
            async (context, token) =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
                var record =
                    await db.Set<TRecord>().FindAsync([recordId], token)
                    ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
                if (!string.Equals(record.CreateBy, identity.UserId, StringComparison.Ordinal))
                    throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
                return await work(db, record, context, token);
            },
            cancellationToken
        );
    }

    private async ValueTask<string?> GetCurrentUserIdAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<TDbContext>().CurrentUserId;
    }

    private async ValueTask<IReadOnlyList<Guid>> ReadRecordIdsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var records = await db.ReadOwnedRecordsAsync<TRecord>(null, cancellationToken);
        return records.Select(record => record.Id).ToArray();
    }

    private async ValueTask<ExtensionOwnedRecordIdentity?> ReadRecordIdentityAsync(
        Guid recordId,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        return (await db.ReadOwnedRecordsAsync<TRecord>(recordId, cancellationToken)).SingleOrDefault();
    }
}

internal sealed record ExtensionOwnedRecordIdentity(Guid Id, string UserId);
