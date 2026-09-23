using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.HumanInteraction.Application;

/// <summary>Shared permission and response rules, independent of SDK, transport and persistence.</summary>
internal static class InteractionRules
{
    public static ToolApprovalDecision? AutomaticallyApprove(InteractionRequest request, AgwPermissionMode? mode) =>
        request is ToolApprovalInteraction && mode == AgwPermissionMode.FullAccess
            ? new ToolApprovalDecision
            {
                InteractionId = request.InteractionId,
                Approved = true,
                Scope = ApprovalScope.AlwaysTool,
            }
            : null;

    /// <summary>
    /// 构造 ResultOnly 下的拒绝答复，让 Agent 拿到结果后继续本轮。
    /// Builds the declining response used under ResultOnly so the Agent continues the turn with a result in hand.
    /// </summary>
    public static InteractionResponse Decline(InteractionRequest request) =>
        request switch
        {
            ToolApprovalInteraction => new ToolApprovalDecision
            {
                InteractionId = request.InteractionId,
                Approved = false,
                Scope = ApprovalScope.Once,
            },
            WorkflowGateInteraction => new WorkflowGateDecision
            {
                InteractionId = request.InteractionId,
                Approved = false,
            },
            _ => new UserInputResponse { InteractionId = request.InteractionId, Cancelled = true },
        };

    public static InteractionResponse ValidateAndNormalize(
        InteractionRequest request,
        InteractionResponse response,
        AgwPermissionMode? mode
    )
    {
        if (!string.Equals(request.InteractionId, response.InteractionId, StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The response does not match this interaction.");
        }

        return (request, response) switch
        {
            (ToolApprovalInteraction, ToolApprovalDecision decision) when Enum.IsDefined(decision.Scope) =>
                decision with
                {
                    Scope = !decision.Approved
                        ? ApprovalScope.Once
                        : mode switch
                        {
                            AgwPermissionMode.FullAccess => ApprovalScope.AlwaysTool,
                            AgwPermissionMode.AlwaysAsk => ApprovalScope.Once,
                            AgwPermissionMode.AllowSameArguments => ApprovalScope.AlwaysArguments,
                            _ => decision.Scope,
                        },
                },
            (WorkflowGateInteraction, WorkflowGateDecision) => response,
            (UserInputInteraction, UserInputResponse { Cancelled: true }) => response,
            (UserInputInteraction, UserInputResponse { ResponseData: { } }) => response,
            _ => throw new AgwException(
                ErrorCodes.InvalidParam,
                "The response kind or content does not match this interaction."
            ),
        };
    }

    public static void RequireInteractiveExecution(InteractionRequest request, HumanInteractionPolicy policy)
    {
        if (policy == HumanInteractionPolicy.Reject)
        {
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                $"Human interaction for '{request.Source.ToolName ?? request.Source.NodeName ?? request.Source.NodeId}' is not supported during unattended execution."
            );
        }
    }
}
