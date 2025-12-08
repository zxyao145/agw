namespace DSystem.Domain.Repositories;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync();
}
