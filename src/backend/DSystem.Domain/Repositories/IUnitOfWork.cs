using System.Threading.Tasks;

namespace DSystem.Domain.Repositories;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync();
}
