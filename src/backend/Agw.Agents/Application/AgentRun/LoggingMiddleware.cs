using System.Runtime.CompilerServices;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Application.AgentRun;

public sealed class LoggingMiddleware 
{
    private readonly ILogger<LoggingMiddleware> _logger;

    public LoggingMiddleware(ILogger<LoggingMiddleware> logger)
    {
        _logger = logger;
    }

    public async IAsyncEnumerable<AgentResponseUpdate> LogStreamingMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var agentName = innerAgent.Name;
        var msgCount = messages.Count();
        _logger.LogInformation("Starting agent run streaming middleware. {agentName} {msgCount}", agentName, msgCount);
        List<AgentResponseUpdate> updates = [];
        await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
        {
            updates.Add(update);
            yield return update;
        }

        _logger.LogInformation("end agent run streaming middleware. {agentName} {responseCount}", agentName, updates.ToAgentResponse().Messages.Count);
    }

    public  async Task<AgentResponse> LogRunMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting agent run middleware.{Messages}", messages);
        var response = await innerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("end agent run middleware.{Response}", response);
        return response;
    }
}
