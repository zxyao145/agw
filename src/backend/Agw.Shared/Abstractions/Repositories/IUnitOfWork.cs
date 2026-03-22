namespace Agw.Shared.Abstractions.Repositories;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync();
}
