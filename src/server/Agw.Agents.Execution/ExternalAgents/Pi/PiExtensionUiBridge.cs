using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiAgentSdk;

namespace Agw.Agents.ExternalAgents.Pi;

internal sealed class PiExtensionUiBridge
{
    private readonly HumanInteractionContextAccessor? _contextAccessor;
    private readonly bool _allowInteraction;
    private IHumanInteractionChannel? _activeChannel;
    private int _isBound;

    public PiExtensionUiBridge(HumanInteractionContextAccessor? contextAccessor, bool allowInteraction)
    {
        _contextAccessor = contextAccessor;
        _allowInteraction = allowInteraction;
    }

    public async Task<AgentResponse> BindRunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken
    )
    {
        using var binding = BindCurrentChannel();
        return await innerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<AgentResponseUpdate> BindRunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var binding = BindCurrentChannel();
        await foreach (
            var update in innerAgent
                .RunStreamingAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return update;
        }
    }

    public async ValueTask<PiExtensionUiResponse> HandleAsync(
        PiExtensionUiRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var channel = Volatile.Read(ref _activeChannel);
        if (channel == null)
        {
            return PiExtensionUiResponse.Cancel(request.Id);
        }

        var payload = JsonSerializer.SerializeToElement(
            new
            {
                request.Method,
                request.Title,
                request.Message,
                request.Options,
                request.Placeholder,
                request.Prefill,
                request.Timeout,
            }
        );
        var prompt = request.Title ?? request.Message ?? "Pi requires user input to continue.";
        var interaction = new HumanInteractionRequest(request.Id, request.Method, prompt, payload)
        {
            ToolName = "PiExtensionUI",
            CallId = request.Id,
        };

        try
        {
            var response = await channel.RequestAsync(interaction, cancellationToken).ConfigureAwait(false);
            if (
                response.Cancelled
                || !string.Equals(response.RequestId, request.Id, StringComparison.Ordinal)
                || !response.ResponseData.HasValue
            )
            {
                return PiExtensionUiResponse.Cancel(request.Id);
            }

            var responseData = response.ResponseData.Value;
            if (
                string.Equals(request.Method, "confirm", StringComparison.Ordinal)
                && responseData.TryGetProperty("confirmed", out var confirmed)
                && confirmed.ValueKind is JsonValueKind.True or JsonValueKind.False
            )
            {
                return new PiExtensionUiResponse { Id = request.Id, Confirmed = confirmed.GetBoolean() };
            }

            if (responseData.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
            {
                var responseValue = value.GetString();
                if (
                    string.Equals(request.Method, "select", StringComparison.Ordinal)
                    && (
                        responseValue == null
                        || request.Options?.Contains(responseValue, StringComparer.Ordinal) != true
                    )
                )
                {
                    return PiExtensionUiResponse.Cancel(request.Id);
                }

                if (request.Method is "select" or "input" or "editor")
                {
                    return new PiExtensionUiResponse { Id = request.Id, Value = responseValue };
                }
            }

            return PiExtensionUiResponse.Cancel(request.Id);
        }
        catch (OperationCanceledException)
        {
            return PiExtensionUiResponse.Cancel(request.Id);
        }
    }

    private IDisposable BindCurrentChannel()
    {
        if (Interlocked.CompareExchange(ref _isBound, 1, 0) != 0)
        {
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "Concurrent Pi runs cannot share a human interaction bridge."
            );
        }

        Volatile.Write(ref _activeChannel, _allowInteraction ? _contextAccessor?.Current : null);
        return new Binding(this);
    }

    private void Unbind()
    {
        Volatile.Write(ref _activeChannel, null);
        Volatile.Write(ref _isBound, 0);
    }

    private sealed class Binding : IDisposable
    {
        private readonly PiExtensionUiBridge _owner;
        private int _disposed;

        public Binding(PiExtensionUiBridge owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Unbind();
            }
        }
    }
}
