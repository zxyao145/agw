using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;

namespace Agw.Agents.Execution.Agents.Runtime;

public partial class AgentRuntimeService
{
    public async Task SetPermissionModeAsync(
        AgentRuntime runtime,
        PermissionMode permissionMode,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(runtime);
        MafSessionApprovalState.Apply(runtime.Session, permissionMode);
        if (runtime.SessionStateScope != null)
        {
            await _sessionStateStore.SaveAsync(
                runtime.AgentType,
                runtime.SessionStateScope,
                runtime.Agent,
                runtime.Session,
                cancellationToken
            );
        }
    }
}
