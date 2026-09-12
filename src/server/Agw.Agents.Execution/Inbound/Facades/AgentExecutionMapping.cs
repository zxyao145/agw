using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Inbound.Facades;

internal static class AgentExecutionMapping
{
    internal static AgwPermissionMode? MapPermissionMode(AgentExecutionPermissionMode? mode) =>
        mode switch
        {
            null => null,
            AgentExecutionPermissionMode.FullAccess => AgwPermissionMode.FullAccess,
            AgentExecutionPermissionMode.AlwaysAsk => AgwPermissionMode.AlwaysAsk,
            AgentExecutionPermissionMode.AllowSameArguments => AgwPermissionMode.AllowSameArguments,
            _ => throw new AgwException(ErrorCodes.InvalidParam, "Unsupported execution permission mode."),
        };

    internal static AgentExecutionResult Map(DurableExecutionOutcome outcome) =>
        new(outcome.ExecutionId, Map(outcome.Status), [], outcome.ErrorMessage);

    internal static AgentExecutionState Map(DurableExecutionStatus status) =>
        status switch
        {
            DurableExecutionStatus.Queued => AgentExecutionState.Queued,
            DurableExecutionStatus.Running or DurableExecutionStatus.Resuming => AgentExecutionState.Running,
            DurableExecutionStatus.WaitingForHuman => AgentExecutionState.WaitingForHuman,
            DurableExecutionStatus.Completed => AgentExecutionState.Completed,
            DurableExecutionStatus.Failed => AgentExecutionState.Failed,
            DurableExecutionStatus.Interrupted => AgentExecutionState.Interrupted,
            _ => throw new AgwException(ErrorCodes.InvalidParam, $"Unsupported execution status '{status}'."),
        };

    internal static void EnsureHumanInteractionAllowed(AgentExecutionRequest request, AgwMessage message)
    {
        if (request.HumanInteractionPolicy == HumanInteractionPolicy.Reject && IsHumanInteraction(message))
        {
            throw new AgwException(ErrorCodes.AgentExecutionFailed, "Human interaction is not supported.");
        }
    }

    internal static bool IsHumanInteraction(AgwMessage message) =>
        AgentExecutionMessageProtocol.GetMessageType(message) is "interaction-request";

    internal static string ExtractText(AgwUserInput input) =>
        string.Join(
            "\n",
            input.Contents.OfType<AgwTextContent>().Select(content => content.Content).Where(value => value != null)
        );
}
