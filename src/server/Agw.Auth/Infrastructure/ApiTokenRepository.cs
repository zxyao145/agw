using Agw.Auth.Application.Persistence;
using Agw.Auth.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Auth.Infrastructure;

public sealed class ApiTokenRepository : IApiTokenRepository
{
    private readonly IAuthDbContext _dbContext;

    public ApiTokenRepository(IAuthDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<bool> ExistsWithNormalizedNameAsync(
        string? ownerUserId,
        string normalizedName,
        CancellationToken cancellationToken
    ) =>
        _dbContext.ApiTokens.AnyAsync(
            token => token.NormalizedName == normalizedName && token.CreateBy == ownerUserId,
            cancellationToken
        );
}
