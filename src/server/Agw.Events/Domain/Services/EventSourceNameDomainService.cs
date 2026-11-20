using Agw.Events.Domain.Repositories;
using Agw.Shared.Exceptions;

namespace Agw.Events.Domain.Services;

public sealed class EventSourceNameDomainService
{
    private readonly IEventSourceNameRepository _repository;

    public EventSourceNameDomainService(IEventSourceNameRepository repository)
    {
        _repository = repository;
    }

    public async Task EnsureAvailableAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (await _repository.ExistsAsync(name, exceptId, cancellationToken))
            throw new AgwException(ErrorCodes.EventSourceNameConflict);
    }

    public async Task<bool> IsAvailableAsync(string name, CancellationToken cancellationToken) =>
        !await _repository.ExistsAsync(name, null, cancellationToken);

    public async Task<string> NextAvailableAsync(string name, CancellationToken cancellationToken)
    {
        var candidate = name;
        for (var sequence = 2; await _repository.ExistsAsync(candidate, null, cancellationToken); sequence++)
            candidate = $"{name}-{sequence}";
        return candidate;
    }
}
