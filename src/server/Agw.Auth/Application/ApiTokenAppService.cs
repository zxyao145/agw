using Agw.Auth.Application.Persistence;
using Agw.Auth.Contracts;
using Agw.Auth.Domain.Behaviors;
using Agw.Auth.Domain.Services;
using Agw.Auth.Security;
using Agw.Shared.Data.Entities.Auth;
using Microsoft.EntityFrameworkCore;

namespace Agw.Auth.Application;

public sealed class ApiTokenAppService : IApiTokenStore
{
    private readonly IAuthDbContext _dbContext;
    private readonly ApiTokenNameUniquenessDomainService _nameUniquenessDomainService;
    private readonly IApiTokenCredentialReader _credentialReader;
    private readonly IUserInfoService _userInfoService;

    public ApiTokenAppService(
        IAuthDbContext dbContext,
        ApiTokenNameUniquenessDomainService nameUniquenessDomainService,
        IApiTokenCredentialReader credentialReader,
        IUserInfoService userInfoService
    )
    {
        _dbContext = dbContext;
        _nameUniquenessDomainService = nameUniquenessDomainService;
        _credentialReader = credentialReader;
        _userInfoService = userInfoService;
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
        return true;
    }

    public Task<ApiTokenIdentity?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default) =>
        _credentialReader.ValidateTokenAsync(token, cancellationToken);
}
