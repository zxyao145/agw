namespace Agw.Shared.Data.Abstractions;

public interface IModuleDbContext
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
