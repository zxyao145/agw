using Agw.Shared.Contracts.Coordination;

namespace Agw.Projects.Contracts.Execution;

public interface IConversationExecutionGate
{
    Task<IApplicationLockLease> AcquireAsync(
        Guid conversationId,
        int expectedGeneration,
        CancellationToken cancellationToken = default
    );
}
