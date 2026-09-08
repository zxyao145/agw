using Agw.Agents.Execution.HumanInteraction.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.HumanInteraction.Approvals;

internal sealed class UnattendedApprovalHandler : IHumanGateApprovalHandler
{
    internal static IHumanGateApprovalHandler Create(PermissionMode? permissionMode) =>
        new PermissionAwareApprovalHandler(new UnattendedApprovalHandler(), permissionMode);

    public ValueTask<HumanGateApprovalDecision> WaitForApprovalAsync(
        HumanGateApprovalRequest request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = request.ToolApprovalRequest is { } tool
            ? $"tool '{ToolApprovalSupport.GetToolName(tool)}'"
            : $"node '{request.NodeName ?? request.NodeId}'";
        throw new AgwException(
            ErrorCodes.AgentExecutionFailed,
            $"Human interaction for {target} is not supported during unattended execution."
        );
    }
}
