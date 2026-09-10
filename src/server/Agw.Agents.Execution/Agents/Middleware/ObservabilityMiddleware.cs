using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Middleware;

public sealed class ObservabilityMiddleware
{
    private readonly ILogger<ObservabilityMiddleware> _logger;

    public ObservabilityMiddleware(ILogger<ObservabilityMiddleware> logger)
    {
        _logger = logger;
    }

    public async IAsyncEnumerable<AgentResponseUpdate> LogStreamingMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var agentName = innerAgent.Name;
        var inputMessages = messages.ToList();
        _logger.LogInformation("Executing agent {AgentName}", agentName);
        _logger.LogDebug("Agent {AgentName} input: {@Input}", agentName, inputMessages);

        List<AgentResponseUpdate> updates = [];
        await foreach (var update in innerAgent.RunStreamingAsync(inputMessages, session, options, cancellationToken))
        {
            updates.Add(update);
            yield return update;
        }

        _logger.LogInformation("Executed agent {AgentName}", agentName);
        _logger.LogDebug("Agent {AgentName} output: {@Output}", agentName, updates.ToAgentResponse());
    }

    public async Task<AgentResponse> LogRunMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken
    )
    {
        var agentName = innerAgent.Name;
        var inputMessages = messages.ToList();
        _logger.LogInformation("Executing agent {AgentName}", agentName);
        _logger.LogDebug("Agent {AgentName} input: {@Input}", agentName, inputMessages);

        var response = await innerAgent
            .RunAsync(inputMessages, session, options, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Executed agent {AgentName}", agentName);
        _logger.LogDebug("Agent {AgentName} output: {@Output}", agentName, response);
        return response;
    }
}
