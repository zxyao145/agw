namespace Agw.Jobs.Domain.Repositories;

public interface IJobRepository
{
    Task<int> CountByOwnerAsync(string ownerUserId);
}
