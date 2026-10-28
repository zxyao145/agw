using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Codex;

internal sealed class CodexProviderSessionTrackingAgent : DelegatingAIAgent
{
    private readonly Guid? _providerSessionId;
    private readonly bool _isResume;

    public CodexProviderSessionTrackingAgent(AIAgent innerAgent, Guid? providerSessionId, bool isResume)
        : base(innerAgent)
    {
        _providerSessionId = providerSessionId;
        _isResume = isResume;
    }

    protected override async ValueTask<AgentSession> CreateSessionCoreAsync(
        CancellationToken cancellationToken = default
    )
    {
        var session = await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!_isResume || _providerSessionId is not { } providerSessionId)
        {
            return session;
        }

        var state = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["threadId"] = providerSessionId.Normalize(),
                ["stateBag"] = session.StateBag.Serialize(),
            }
        );
        return await InnerAgent
            .DeserializeSessionAsync(state, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default
    ) => InnerAgent.SerializeSessionAsync(session, jsonSerializerOptions, cancellationToken);

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default
    ) => InnerAgent.DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken);

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var response = await InnerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        if (response.RawRepresentation is IEnumerable<OpenAI.CodexSdk.AppServer.AppServerNotification> notifications)
        {
            foreach (var message in response.Messages.Where(message => message.Role == ChatRole.Assistant))
            {
                message.RawRepresentation = notifications;
            }
        }

        return response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await foreach (
            var update in InnerAgent
                .RunStreamingAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return update;
        }
    }
}
