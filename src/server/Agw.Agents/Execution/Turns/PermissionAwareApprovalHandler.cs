using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Commands.Setting;

namespace Agw.Agents.Execution.Turns;

internal sealed class PermissionAwareApprovalHandler : IHumanGateApprovalHandler
{
    private readonly IHumanGateApprovalHandler _inner;
    private readonly HumanGateApprovalCoordinator? _coordinator;
    private readonly PermissionModeState _permissionState;

    public PermissionAwareApprovalHandler(
        IHumanGateApprovalHandler inner,
        PermissionMode? permissionMode)
        : this(inner, new PermissionModeState(permissionMode))
    {
    }

    public PermissionAwareApprovalHandler(
        IHumanGateApprovalHandler inner,
        PermissionModeState permissionState)
    {
        _inner = inner;
        _coordinator = inner as HumanGateApprovalCoordinator;
        _permissionState = permissionState;
    }

    public void SetPermissionMode(PermissionMode permissionMode)
    {
        _permissionState.Set(permissionMode);
        if (permissionMode == PermissionMode.FullAccess)
        {
            _coordinator?.ApprovePendingToolRequests();
        }
    }

    public bool RequiresHumanResponse(HumanGateApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.ToolApprovalRequest == null ||
            _permissionState.Current != PermissionMode.FullAccess;
    }

    public async ValueTask<HumanGateApprovalDecision> WaitForApprovalAsync(
        HumanGateApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ToolApprovalRequest != null &&
            _permissionState.Current == PermissionMode.FullAccess)
        {
            return CreateFullAccessDecision(request);
        }

        var pendingDecision = _inner.WaitForApprovalAsync(request, cancellationToken);
        if (request.ToolApprovalRequest != null &&
            _permissionState.Current == PermissionMode.FullAccess)
        {
            _coordinator?.ApprovePendingToolRequests();
        }

        var decision = await pendingDecision;
        if (!decision.Approved || request.ToolApprovalRequest == null)
        {
            return decision;
        }

        return decision with
        {
            ApprovalScope = _permissionState.Current switch
            {
                PermissionMode.FullAccess => "always-tool",
                PermissionMode.AlwaysAsk => "once",
                PermissionMode.AllowSameArguments => "always-arguments",
                _ => decision.ApprovalScope,
            }
        };
    }

    private static HumanGateApprovalDecision CreateFullAccessDecision(
        HumanGateApprovalRequest request) =>
        new(
            request.RequestId,
            Approved: true,
            ResponseText: null,
            ApprovalScope: "always-tool");
}
