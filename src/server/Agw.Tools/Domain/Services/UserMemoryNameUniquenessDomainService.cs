using Agw.Shared.Exceptions;
using Agw.Tools.Domain.Repositories;

namespace Agw.Tools.Domain.Services;

/// <summary>
/// <para>同一用户的记忆名称按规范化形式唯一。</para>
/// <para>A user's memory names are unique by their normalized form.</para>
/// </summary>
public sealed class UserMemoryNameUniquenessDomainService
{
    private readonly IUserMemoryRepository _memories;

    public UserMemoryNameUniquenessDomainService(IUserMemoryRepository memories)
    {
        _memories = memories;
    }

    public async Task EnsureNameAvailableAsync(UserMemory memory, CancellationToken cancellationToken)
    {
        var exists = await _memories
            .ExistsWithNormalizedNameAsync(memory.UserId, memory.NormalizedName, memory.Id, cancellationToken)
            .ConfigureAwait(false);
        if (exists)
        {
            throw new AgwException(ErrorCodes.UserMemoryNameAlreadyExists);
        }
    }
}
