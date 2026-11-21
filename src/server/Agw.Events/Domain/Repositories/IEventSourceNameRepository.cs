namespace Agw.Events.Domain.Repositories;

public interface IEventSourceNameRepository
{
    Task<bool> ExistsAsync(string name, Guid? exceptId, CancellationToken cancellationToken);
}
