using Agw.Tools.Application.Persistence;
using Agw.Tools.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tools.Infrastructure;

public sealed class UserMemoryRepository : IUserMemoryRepository
{
    private readonly IToolsDbContext _dbContext;

    public UserMemoryRepository(IToolsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<bool> ExistsWithNormalizedNameAsync(
        string userId,
        string normalizedName,
        Guid excludedMemoryId,
        CancellationToken cancellationToken
    ) =>
        _dbContext
            .UserMemories.AsNoTracking()
            .AnyAsync(
                memory =>
                    memory.UserId == userId && memory.NormalizedName == normalizedName && memory.Id != excludedMemoryId,
                cancellationToken
            );
}
