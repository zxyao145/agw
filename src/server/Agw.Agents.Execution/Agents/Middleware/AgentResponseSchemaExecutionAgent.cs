using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware;

/// <summary>
/// Telemetry decorator for agents with a configured response schema. Counts one execution per turn;
/// failures include thrown exceptions (except consumer cancellation) and fatal error content such as
/// a missing structured final result. Forwards disposal so owning chains keep releasing processes
/// and temporary resources.
/// </summary>
internal sealed class AgentResponseSchemaExecutionAgent : DelegatingAIAgent, IAsyncDisposable
{
    private readonly string _agentType;
    private readonly string _externalAgentKind;
    private readonly string _providerType;

    public AgentResponseSchemaExecutionAgent(
        AIAgent innerAgent,
        string agentType,
        string externalAgentKind,
        string providerType
    )
        : base(innerAgent)
    {
        _agentType = agentType;
        _externalAgentKind = externalAgentKind;
        _providerType = providerType;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var failed = false;
        try
        {
            var response = await InnerAgent
                .RunAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false);
            failed = HasFatalError(response.Messages.SelectMany(message => message.Contents));
            return response;
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            failed = true;
            throw;
        }
        finally
        {
            AgentResponseSchemaTelemetry.Record(_agentType, _externalAgentKind, _providerType, failed);
        }
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var failed = false;
        var enumerator = InnerAgent
            .RunStreamingAsync(messages, session, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                AgentResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = enumerator.Current;
                }
                catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
                {
                    failed = true;
                    throw;
                }

                if (!failed && HasFatalError(update.Contents))
                {
                    failed = true;
                }

                yield return update;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            AgentResponseSchemaTelemetry.Record(_agentType, _externalAgentKind, _providerType, failed);
        }
    }

    public async ValueTask DisposeAsync()
    {
        switch (InnerAgent)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static bool HasFatalError(IEnumerable<AIContent> contents) =>
        contents
            .OfType<ErrorContent>()
            .Any(content =>
                content.AdditionalProperties?.TryGetValue("isFatalError", out var value) == true && value is true
            );
}
