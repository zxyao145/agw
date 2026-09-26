using Agw.Auth.Application.Persistence;
using Agw.Auth.Contracts;
using Agw.Auth.Domain.Behaviors;
using Agw.Auth.Domain.Services;
using Agw.Auth.Security;
using Agw.Shared.Data.Entities.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Agw.Auth.Application;

public sealed class ApiTokenAppService : IApiTokenStore
{
    private const string ValidationCacheKeyPrefix = "agw:auth:api-token:";

    /// <summary>
    /// <para>成功验证的身份缓存 30 秒；撤销时按 Token 标签清除本实例缓存。</para>
    /// <para>Successfully validated identities are cached for 30 seconds; revocation clears this instance's entries by token tag.</para>
    /// </summary>
    private static readonly HybridCacheEntryOptions ValidationCacheEntry = new()
    {
        Expiration = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };

    private static readonly HybridCacheEntryOptions ValidationCacheRead = new()
    {
        Flags =
            HybridCacheEntryFlags.DisableUnderlyingData
            | HybridCacheEntryFlags.DisableLocalCacheWrite
            | HybridCacheEntryFlags.DisableDistributedCacheWrite,
    };

    private readonly IAuthDbContext _dbContext;
    private readonly ApiTokenNameUniquenessDomainService _nameUniquenessDomainService;
    private readonly IApiTokenCredentialReader _credentialReader;
    private readonly IUserInfoService _userInfoService;
    private readonly HybridCache _cache;

    public ApiTokenAppService(
        IAuthDbContext dbContext,
        ApiTokenNameUniquenessDomainService nameUniquenessDomainService,
        IApiTokenCredentialReader credentialReader,
        IUserInfoService userInfoService,
        HybridCache cache
    )
    {
        _dbContext = dbContext;
        _nameUniquenessDomainService = nameUniquenessDomainService;
        _credentialReader = credentialReader;
        _userInfoService = userInfoService;
        _cache = cache;
    }

    public async Task<IReadOnlyList<ApiTokenSummary>> ListTokensAsync(CancellationToken cancellationToken = default)
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        var tokens = await _dbContext
            .ApiTokens.AsNoTracking()
            .Where(token => token.CreateBy == ownerUserId)
            .Select(token => new ApiTokenSummary(token.Id, token.Name, token.Prefix, token.CreateTime))
            .ToArrayAsync(cancellationToken);
        return tokens.OrderByDescending(token => token.CreatedAt).ToArray();
    }

    public async Task<CreatedApiToken> CreateTokenAsync(string name, CancellationToken cancellationToken = default)
    {
        var secret = ApiTokenSecret.Create();
        var token = new ApiToken
        {
            Id = Guid.CreateVersion7(),
            Prefix = ApiTokenSecret.GetLookupPrefix(secret),
            SecretHash = ApiTokenSecret.Hash(secret),
            CreateBy = _userInfoService.RequiredUserId,
        };
        new ApiTokenBehavior(token).AssignName(name);
        await _nameUniquenessDomainService.EnsureNameAvailableAsync(token, cancellationToken);
        _dbContext.ApiTokens.Add(token);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 并发创建同名 Token 时唯一索引拒绝写入，此时报告名称冲突；其他写入失败保持原异常。
            // A concurrent same-name token trips the unique index and reports the name conflict; other write failures keep the original exception.
            _dbContext.ApiTokens.Entry(token).State = EntityState.Detached;
            await _nameUniquenessDomainService.EnsureNameAvailableAsync(token, cancellationToken);
            throw;
        }

        return new CreatedApiToken(token.Id, token.Name, token.Prefix, token.CreateTime, secret);
    }

    public async Task<bool> RevokeTokenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        var token = await _dbContext.ApiTokens.FirstOrDefaultAsync(
            candidate => candidate.Id == id && candidate.CreateBy == ownerUserId,
            cancellationToken
        );
        if (token == null)
        {
            return false;
        }

        _dbContext.ApiTokens.Remove(token);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _cache.RemoveByTagAsync(GetTokenTag(id), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// <para>先读取缓存中已验证的身份；未命中时查询数据库，只缓存验证成功的结果。</para>
    /// <para>Reads a validated identity from the cache first; on a miss the database is queried and only successful validations are cached.</para>
    /// </summary>
    public async Task<ApiTokenIdentity?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (
            string.IsNullOrWhiteSpace(token) || !token.StartsWith(ApiTokenSecret.SecretPrefix, StringComparison.Ordinal)
        )
        {
            return null;
        }

        var key = ValidationCacheKeyPrefix + ApiTokenSecret.Hash(token);
        var cached = await _cache.GetOrCreateAsync(
            key,
            static _ => ValueTask.FromResult<ApiTokenIdentity?>(null),
            ValidationCacheRead,
            cancellationToken: cancellationToken
        );
        if (cached != null)
        {
            return cached;
        }

        var identity = await _credentialReader.ValidateTokenAsync(token, cancellationToken);
        if (identity?.TokenId is { } tokenId)
        {
            await _cache.SetAsync(key, identity, ValidationCacheEntry, [GetTokenTag(tokenId)], cancellationToken);
        }

        return identity;
    }

    private static string GetTokenTag(Guid tokenId) => $"{ValidationCacheKeyPrefix}{tokenId:N}";
}
