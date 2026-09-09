using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.HumanInteraction.Application;

/// <summary>Shared permission and response rules, independent of SDK, transport and persistence.</summary>
internal static class InteractionRules
{
    public static ToolApprovalDecision? AutomaticallyApprove(InteractionRequest request, PermissionMode? mode) =>
        request is ToolApprovalInteraction && mode == PermissionMode.FullAccess
            ? new ToolApprovalDecision
            {
                InteractionId = request.InteractionId,
                Approved = true,
                Scope = ApprovalScope.AlwaysTool,
            }
            : null;

    public static InteractionResponse ValidateAndNormalize(
        InteractionRequest request,
        InteractionResponse response,
        PermissionMode? mode
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
                            PermissionMode.FullAccess => ApprovalScope.AlwaysTool,
                            PermissionMode.AlwaysAsk => ApprovalScope.Once,
                            PermissionMode.AllowSameArguments => ApprovalScope.AlwaysArguments,
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
