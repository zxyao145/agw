using Agw.Shared.Contracts.Coordination;

namespace Agw.Shared.Coordination;

public static class AgentDefinitionLock
{
    public static string GetResourceName(string ownerUserId) => $"agent-definitions:{ownerUserId}";

    public static async Task<ApplicationLockScope> AcquireScopeAsync(
        IApplicationLock applicationLock,
        string ownerUserId,
        CancellationToken cancellationToken
    )
    {
        var lease = await applicationLock.AcquireAsync(GetResourceName(ownerUserId), cancellationToken);
        return new ApplicationLockScope(lease, cancellationToken);
    }
}
