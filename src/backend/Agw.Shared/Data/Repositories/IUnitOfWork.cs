namespace Agw.Shared.Data.Repositories;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync();
}
