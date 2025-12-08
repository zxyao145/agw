using System.Threading.Tasks;
using DSystem.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace DSystem.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly DbContext _dbContext;

    public UnitOfWork(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<int> SaveChangesAsync() => _dbContext.SaveChangesAsync();
}
