using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.ExternalAgents.ClaudeCode;

/// <summary>
/// Captures the provider session ID from Claude Code init messages without changing agent responses.
/// </summary>
internal sealed class ClaudeCodeProviderSessionTrackingAgent : DelegatingAIAgent
{
    private readonly Func<string, CancellationToken, ValueTask> _onProviderSessionStartedAsync;
    private int _providerSessionCaptured;

    public ClaudeCodeProviderSessionTrackingAgent(
        AIAgent innerAgent,
        Func<string, CancellationToken, ValueTask> onProviderSessionStartedAsync
    )
        : base(innerAgent)
    {
        _onProviderSessionStartedAsync = onProviderSessionStartedAsync;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var response = await InnerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        foreach (var message in response.Messages)
        {
            await CaptureProviderSessionIdAsync(message.AdditionalProperties, message.Contents).ConfigureAwait(false);
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
            await CaptureProviderSessionIdAsync(update.AdditionalProperties, update.Contents).ConfigureAwait(false);
            yield return update;
        }
    }

    private async ValueTask CaptureProviderSessionIdAsync(
        AdditionalPropertiesDictionary? additionalProperties,
        IEnumerable<AIContent> contents
    )
    {
        if (
            Volatile.Read(ref _providerSessionCaptured) != 0
            || !TryGetProviderSessionId(additionalProperties, contents, out var providerSessionId)
            || Interlocked.CompareExchange(ref _providerSessionCaptured, 1, 0) != 0
        )
        {
            return;
        }

        await _onProviderSessionStartedAsync(providerSessionId, CancellationToken.None).ConfigureAwait(false);
    }

    private static bool TryGetProviderSessionId(
        AdditionalPropertiesDictionary? additionalProperties,
        IEnumerable<AIContent> contents,
        out string providerSessionId
    )
    {
        providerSessionId = string.Empty;
        if (
            additionalProperties?.TryGetValue("subtype", out var subtype) != true
            || !string.Equals(subtype?.ToString(), "init", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        var json = contents.OfType<TextContent>().FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (
                document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("session_id", out var sessionIdElement)
                || sessionIdElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(sessionIdElement.GetString(), out var sessionId)
                || sessionId == Guid.Empty
            )
            {
                return false;
            }

            providerSessionId = sessionId.Normalize();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
