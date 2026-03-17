namespace Agw.Domain.Repositories;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync();
}
