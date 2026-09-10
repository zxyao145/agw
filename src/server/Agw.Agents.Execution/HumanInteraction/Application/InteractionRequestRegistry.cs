using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.HumanInteraction.Application;

internal sealed class InteractionRequestRegistry : IInteractionRequestRegistry
{
    private readonly ConcurrentDictionary<(string ScopeId, string ProviderId), UserInputInteraction> _requests = new();

    public InteractionRequestRegistry(IEnumerable<UserInputInteraction>? requests = null)
    {
        foreach (var request in requests ?? [])
        {
            if (
                (request.Source.ProviderScopeId ?? request.Source.NodeId) is not { } scope
                || request.Source.ProviderRequestId is not { } id
                || !_requests.TryAdd((scope, id), request)
            )
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "The saved interaction catalog is invalid."
                );
        }
    }

    public UserInputInteraction Register(string providerRequestId, UserInputRequest request)
    {
        var source = request.Source with
        {
            NodeId = request.Source.NodeId ?? "standalone",
            ProviderRequestId = providerRequestId,
        };
        var saved = _requests.GetOrAdd(
            (source.ProviderScopeId ?? source.NodeId, providerRequestId),
            _ => new UserInputInteraction
            {
                InteractionId = Guid.CreateVersion7().ToString("N"),
                Source = source,
                Prompt = request.Prompt,
                InputKind = request.InputKind,
                Payload = request.Payload.Clone(),
                Arguments = request.Arguments?.Clone(),
            }
        );
        if (
            saved.Source != source
            || saved.InputKind != request.InputKind
            || !JsonNode.DeepEquals(
                JsonNode.Parse(saved.Payload.GetRawText()),
                JsonNode.Parse(request.Payload.GetRawText())
            )
        )
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "The SDK reused an interaction identity for different input."
            );
        return saved;
    }

    public UserInputInteraction? Find(string providerRequestId, string scopeId) =>
        _requests.GetValueOrDefault((scopeId, providerRequestId));

    public bool IsUserInputCall(string nodeId, string callId) =>
        _requests.Values.Any(request => request.Source.NodeId == nodeId && request.Source.CallId == callId);

    public IReadOnlyList<UserInputInteraction> Snapshot() =>
        _requests.Values.OrderBy(item => item.InteractionId, StringComparer.Ordinal).ToArray();
}
