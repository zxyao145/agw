using System.Diagnostics;
using System.Runtime.CompilerServices;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Middleware;

public sealed class ObservabilityMiddleware
{
    private const string WorkflowActivitySourceName = "Microsoft.Agents.AI.Workflows";
    private const string ExecutorProcessActivityName = "executor.process";

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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var agentName = innerAgent.Name;
        var inputMessages = messages.ToList();
        TagCurrentWorkflowExecutor(agentName);
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
        CancellationToken cancellationToken)
    {
        var agentName = innerAgent.Name;
        var inputMessages = messages.ToList();
        TagCurrentWorkflowExecutor(agentName);
        _logger.LogInformation("Executing agent {AgentName}", agentName);
        _logger.LogDebug("Agent {AgentName} input: {@Input}", agentName, inputMessages);

        var response = await innerAgent.RunAsync(inputMessages, session, options, cancellationToken).ConfigureAwait(false);
        
        _logger.LogInformation("Executed agent {AgentName}", agentName);
        _logger.LogDebug("Agent {AgentName} output: {@Output}", agentName, response);
        return response;
    }

    private static void TagCurrentWorkflowExecutor(string? agentName)
    {
        var activity = Activity.Current;
        if (activity?.Source.Name == WorkflowActivitySourceName &&
            activity.OperationName.StartsWith(ExecutorProcessActivityName, StringComparison.Ordinal))
        {
            activity.SetTag("gen_ai.agent.name", agentName);
        }
    }
}
