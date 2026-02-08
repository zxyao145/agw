namespace DSystem.Shared.Repositories;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync();
}
