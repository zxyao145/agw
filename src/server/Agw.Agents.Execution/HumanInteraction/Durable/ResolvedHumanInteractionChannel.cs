using System.Text.Json.Nodes;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.HumanInteraction.Durable;

/// <summary>Replays an answer only for the exact node and tool call that requested it.</summary>
internal sealed class ResolvedHumanInteractionChannel : IHumanInteractionChannel
{
    private readonly IReadOnlyList<DurableResolvedInteraction> _interactions;

    public ResolvedHumanInteractionChannel(IReadOnlyList<DurableResolvedInteraction> interactions) =>
        _interactions = interactions;

    public ValueTask<UserInputResponse> RequestAsync(UserInputRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Source.CallId) || string.IsNullOrWhiteSpace(request.Source.NodeId))
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "A restored input requires its original node and call identity."
            );
        var matches = _interactions
            .Where(item =>
                item.Request is UserInputInteraction input
                && input.Source.CallId == request.Source.CallId
                && input.Source.NodeId == request.Source.NodeId
                && input.Source.ToolName == request.Source.ToolName
                && input.InputKind == request.InputKind
            )
            .ToArray();
        if (
            matches.Length != 1
            || matches[0].Response is not UserInputResponse response
            || !JsonNode.DeepEquals(
                JsonNode.Parse(((UserInputInteraction)matches[0].Request).Payload.GetRawText()),
                JsonNode.Parse(request.Payload.GetRawText())
            )
        )
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "No unique saved answer matches this input request."
            );
        InteractionRules.ValidateAndNormalize(matches[0].Request, response, mode: null);
        return ValueTask.FromResult(response);
    }
}
