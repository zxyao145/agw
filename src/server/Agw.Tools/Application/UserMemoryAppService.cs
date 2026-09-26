using Agw.Auth.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Exceptions;
using Agw.Tools.Application.Persistence;
using Agw.Tools.Domain.Behaviors;
using Agw.Tools.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tools.Application;

public sealed record UserMemorySummary(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreateTime,
    DateTimeOffset? UpdateTime
);

public sealed record UserMemoryDetails(
    Guid Id,
    string Name,
    string? Description,
    string Content,
    DateTimeOffset CreateTime,
    DateTimeOffset? UpdateTime
);

public sealed record UserMemoryContextEntry(string Name, string Content);

public sealed class UserMemoryAppService
{
    public const int MaxUserIdLength = 256;

    private static readonly int[] SupportedPageSizes = [10, 20, 50];

    private readonly IToolsDbContext _dbContext;
    private readonly UserMemoryNameUniquenessDomainService _nameUniquenessDomainService;
    private readonly IApplicationLock _applicationLock;
    private readonly IUserInfoService _userInfoService;

    public UserMemoryAppService(
        IToolsDbContext dbContext,
        UserMemoryNameUniquenessDomainService nameUniquenessDomainService,
        IApplicationLock applicationLock,
        IUserInfoService userInfoService
    )
    {
        _dbContext = dbContext;
        _nameUniquenessDomainService = nameUniquenessDomainService;
        _applicationLock = applicationLock;
        _userInfoService = userInfoService;
    }

    public async Task<PagedResult<UserMemorySummary>> ListPageAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedUserId = GetCurrentUserId();
        ValidatePaging(pageIndex, pageSize);

        var query = _dbContext.UserMemories.AsNoTracking().Where(memory => memory.UserId == normalizedUserId);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<UserMemorySummary> items;
        try
        {
            items = await query
                .OrderByDescending(memory => memory.UpdateTime ?? memory.CreateTime)
                .ThenByDescending(memory => memory.Id)
                .Skip((pageIndex - 1) * pageSize)
                .Take(pageSize)
                .Select(memory => new UserMemorySummary(
                    memory.Id,
                    memory.Name,
                    memory.Description,
                    memory.CreateTime,
                    memory.UpdateTime
                ))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsDateTimeOffsetQueryTranslationException(exception))
        {
            var summaries = await query
                .Select(memory => new UserMemorySummary(
                    memory.Id,
                    memory.Name,
                    memory.Description,
                    memory.CreateTime,
                    memory.UpdateTime
                ))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            items = summaries
                .OrderByDescending(memory => memory.UpdateTime ?? memory.CreateTime)
                .ThenByDescending(memory => memory.Id)
                .Skip((pageIndex - 1) * pageSize)
                .Take(pageSize)
                .ToList();
        }

        return new PagedResult<UserMemorySummary>
        {
            Items = items,
            Total = total,
            PageIndex = pageIndex,
            PageSize = pageSize,
        };
    }

    public async Task<IReadOnlyList<UserMemorySummary>> ListIndexAsync(
        int? limit = null,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedUserId = GetCurrentUserId();
        if (limit.HasValue && limit.Value <= 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "limit must be positive.");
        }

        var query = _dbContext
            .UserMemories.AsNoTracking()
            .Where(memory => memory.UserId == normalizedUserId)
            .OrderBy(memory => memory.Name)
            .Select(memory => new UserMemorySummary(
                memory.Id,
                memory.Name,
                memory.Description,
                memory.CreateTime,
                memory.UpdateTime
            ));
        if (limit.HasValue)
        {
            query = query.Take(limit.Value);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserMemoryContextEntry>> ListContextAsync(
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        if (limit <= 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "limit must be positive.");
        }

        var userId = GetCurrentUserId();
        var memories = await _dbContext
            .UserMemories.AsNoTracking()
            .Where(memory => memory.UserId == userId)
            .OrderBy(memory => memory.Name)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return memories.Select(memory => new UserMemoryContextEntry(memory.Name, memory.Content)).ToList();
    }

    public async Task<UserMemoryDetails?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var normalizedUserId = GetCurrentUserId();
        var memory = await _dbContext
            .UserMemories.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id && item.UserId == normalizedUserId, cancellationToken)
            .ConfigureAwait(false);
        return memory == null ? null : MapDetails(memory);
    }

    public async Task<UserMemoryDetails?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalizedUserId = GetCurrentUserId();
        var normalizedName = UserMemoryBehavior.NormalizeName(name).Normalized;
        var memory = await _dbContext
            .UserMemories.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.UserId == normalizedUserId && item.NormalizedName == normalizedName,
                cancellationToken
            )
            .ConfigureAwait(false);
        return memory == null ? null : MapDetails(memory);
    }

    public async Task<UserMemoryDetails> CreateAsync(
        string name,
        string? description,
        string content,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedUserId = GetCurrentUserId();
        var memory = new UserMemory { Id = Guid.CreateVersion7(), UserId = normalizedUserId };
        new UserMemoryBehavior(memory).Define(name, description, content);

        await using var lease = await AcquireMutationLockAsync(normalizedUserId, cancellationToken)
            .ConfigureAwait(false);
        await _nameUniquenessDomainService.EnsureNameAvailableAsync(memory, cancellationToken).ConfigureAwait(false);
        await _dbContext.UserMemories.AddAsync(memory, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return MapDetails(memory);
    }

    public async Task<UserMemoryDetails?> UpdateAsync(
        Guid id,
        string name,
        string? description,
        string content,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedUserId = GetCurrentUserId();
        await using var lease = await AcquireMutationLockAsync(normalizedUserId, cancellationToken)
            .ConfigureAwait(false);
        var memory = await _dbContext
            .UserMemories.SingleOrDefaultAsync(
                item => item.Id == id && item.UserId == normalizedUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (memory == null)
        {
            return null;
        }

        new UserMemoryBehavior(memory).Define(name, description, content);
        await _nameUniquenessDomainService.EnsureNameAvailableAsync(memory, cancellationToken).ConfigureAwait(false);
        _dbContext.UserMemories.Entry(memory).Property(item => item.Name).IsModified = true;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return MapDetails(memory);
    }

    public async Task<UserMemoryDetails> UpsertByNameAsync(
        string name,
        string content,
        string? description,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedUserId = GetCurrentUserId();
        var normalizedName = UserMemoryBehavior.NormalizeName(name).Normalized;

        await using var lease = await AcquireMutationLockAsync(normalizedUserId, cancellationToken)
            .ConfigureAwait(false);
        var memory = await _dbContext
            .UserMemories.SingleOrDefaultAsync(
                item => item.UserId == normalizedUserId && item.NormalizedName == normalizedName,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (memory == null)
        {
            memory = new UserMemory { Id = Guid.CreateVersion7(), UserId = normalizedUserId };
            new UserMemoryBehavior(memory).Define(name, description, content);
            await _dbContext.UserMemories.AddAsync(memory, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            new UserMemoryBehavior(memory).RewriteByName(name, content, description);
            _dbContext.UserMemories.Entry(memory).Property(item => item.Name).IsModified = true;
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return MapDetails(memory);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var normalizedUserId = GetCurrentUserId();
        await using var lease = await AcquireMutationLockAsync(normalizedUserId, cancellationToken)
            .ConfigureAwait(false);
        var memory = await _dbContext
            .UserMemories.SingleOrDefaultAsync(
                item => item.Id == id && item.UserId == normalizedUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
        return memory != null && await DeleteCoreAsync(memory, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalizedUserId = GetCurrentUserId();
        var normalizedName = UserMemoryBehavior.NormalizeName(name).Normalized;
        await using var lease = await AcquireMutationLockAsync(normalizedUserId, cancellationToken)
            .ConfigureAwait(false);
        var memory = await _dbContext
            .UserMemories.SingleOrDefaultAsync(
                item => item.UserId == normalizedUserId && item.NormalizedName == normalizedName,
                cancellationToken
            )
            .ConfigureAwait(false);
        return memory != null && await DeleteCoreAsync(memory, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> DeleteCoreAsync(UserMemory memory, CancellationToken cancellationToken)
    {
        _dbContext.UserMemories.Remove(memory);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private Task<IApplicationLockLease> AcquireMutationLockAsync(string userId, CancellationToken cancellationToken) =>
        _applicationLock.AcquireAsync($"user-memory:{userId}", cancellationToken);

    private string GetCurrentUserId()
    {
        return NormalizeUserId(_userInfoService.RequiredUserId);
    }

    private static string NormalizeUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new AgwException(ErrorCodes.AuthenticationRequired);
        }

        var normalized = userId.Trim();
        if (normalized.Length > MaxUserIdLength)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "User id is too long.");
        }

        return normalized;
    }

    private static void ValidatePaging(int pageIndex, int pageSize)
    {
        if (pageIndex < 1)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "pageIndex must be at least 1.");
        }

        if (!SupportedPageSizes.Contains(pageSize))
        {
            throw new AgwException(ErrorCodes.InvalidPageSize, "pageSize must be one of 10, 20, or 50.");
        }
    }

    private static bool IsDateTimeOffsetQueryTranslationException(Exception exception) =>
        exception is NotSupportedException
        && exception.Message.Contains(
            "SQLite does not support expressions of type 'DateTimeOffset'",
            StringComparison.Ordinal
        );

    private static UserMemoryDetails MapDetails(UserMemory memory) =>
        new(memory.Id, memory.Name, memory.Description, memory.Content, memory.CreateTime, memory.UpdateTime);
}
