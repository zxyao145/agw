using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;

/// <summary>Persists Agw grants at the SDK call boundary, without modifying its approval queue.</summary>
internal sealed class MafApprovalGrantAgent : DelegatingAIAgent
{
    private readonly HumanInteractionContextAccessor? _interactions;

    public MafApprovalGrantAgent(AIAgent innerAgent, HumanInteractionContextAccessor? interactions)
        : base(innerAgent)
    {
        _interactions = interactions;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var input = messages.ToArray();
        await ApplyAsync(input, session, cancellationToken).ConfigureAwait(false);
        return await InnerAgent.RunAsync(input, session, options, cancellationToken).ConfigureAwait(false);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var input = messages.ToArray();
        await ApplyAsync(input, session, cancellationToken).ConfigureAwait(false);
        await foreach (
            var update in InnerAgent.RunStreamingAsync(input, session, options, cancellationToken).ConfigureAwait(false)
        )
            yield return update;
    }

    private async ValueTask ApplyAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        CancellationToken cancellationToken
    )
    {
        if (_interactions is not null)
            await _interactions.RefreshPermissionsAsync(cancellationToken).ConfigureAwait(false);
        if (session is null)
            return;
        var mode = _interactions?.PermissionState is { } permissions
            ? MafSessionApprovalState.Synchronize(session, permissions)
            : MafSessionApprovalState.GetPermissionMode(session);
        MafSessionApprovalState.Apply(session, mode);
        foreach (var response in messages.SelectMany(message => message.Contents).OfType<ToolApprovalResponseContent>())
        {
            if (
                response.Approved
                && response.ToolCall is FunctionCallContent call
                && response.AdditionalProperties?.TryGetValue(MafApprovalAdapter.GrantScopeProperty, out var value)
                    == true
                && Enum.TryParse<ApprovalScope>(value?.ToString(), out var scope)
            )
                MafSessionApprovalState.Record(session, call, scope, mode);
        }
    }
}
