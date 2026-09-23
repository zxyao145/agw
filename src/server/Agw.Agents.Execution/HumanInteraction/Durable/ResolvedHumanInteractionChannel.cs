using System.Text.Json.Nodes;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.HumanInteraction.Durable;

/// <summary>Replays an answer only for the exact node and tool call that requested it.</summary>
internal sealed class ResolvedHumanInteractionChannel : IHumanInteractionChannel
{
    private readonly IReadOnlyList<DurableResolvedInteraction> _interactions;
    private readonly bool _allowInteraction;

    public ResolvedHumanInteractionChannel(
        IReadOnlyList<DurableResolvedInteraction> interactions,
        bool allowInteraction = true
    )
    {
        _interactions = interactions;
        _allowInteraction = allowInteraction;
    }

    public ValueTask<UserInputResponse> RequestAsync(UserInputRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 交互不可用时答复取消，调用方按协议返回取消结果并继续本轮。
        // Answer with a cancellation when interaction is unavailable; the caller returns its cancelled result and continues.
        if (!_allowInteraction)
            return ValueTask.FromResult(
                new UserInputResponse { InteractionId = Guid.CreateVersion7().ToString("N"), Cancelled = true }
            );
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
