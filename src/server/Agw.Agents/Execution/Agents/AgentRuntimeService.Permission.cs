using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;

namespace Agw.Agents.Execution.Agents;

public partial class AgentRuntimeService
{
    public async Task SetPermissionModeAsync(
        AgentRuntime runtime,
        PermissionMode permissionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ToolApprovalPermissionState.Apply(runtime.Session, permissionMode);
        if (runtime.SessionStateScope != null)
        {
            await _sessionStateStore.SaveAsync(
                runtime.AgentType,
                runtime.SessionStateScope,
                runtime.Agent,
                runtime.Session,
                cancellationToken);
        }
    }
}
