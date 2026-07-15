using System.Runtime.CompilerServices;

using Agw.Shared.Contracts.Projects;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Middleware;

public sealed class UsageTrackingMiddleware
{
    private const string UnknownAgentName = "$unknown";

    private readonly IProviderSessionState _providerSessionState;
    private readonly IAgentUsageRecorder _usageRecorder;
    private readonly ILogger<UsageTrackingMiddleware> _logger;

    public UsageTrackingMiddleware(
        IProviderSessionState providerSessionState,
        IAgentUsageRecorder usageRecorder,
        ILogger<UsageTrackingMiddleware> logger)
    {
        _providerSessionState = providerSessionState;
        _usageRecorder = usageRecorder;
        _logger = logger;
    }

    public async IAsyncEnumerable<AgentResponseUpdate> TrackStreamingMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var combinedUsage = new UsageDetails();
        var hasUsage = false;

        try
        {
            await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
            {
                foreach (var usageContent in update.Contents.OfType<UsageContent>())
                {
                    combinedUsage.Add(usageContent.Details);
                    hasUsage = true;
                }

                yield return update;
            }
        }
        finally
        {
            if (hasUsage)
            {
                await RecordAsync(session, ResolveAgentName(innerAgent), combinedUsage);
            }
        }
    }

    public async Task<AgentResponse> TrackRunMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var response = await innerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        if (response.Usage != null)
        {
            await RecordAsync(session, ResolveAgentName(innerAgent), response.Usage);
        }

        return response;
    }

    private async Task RecordAsync(AgentSession? session, string agentName, UsageDetails usage)
    {
        if (session == null ||
            !_providerSessionState.TryGetProjectContext(session, out var projectId, out var contextId))
        {
            return;
        }

        try
        {
            await _usageRecorder.AddAsync(
                projectId,
                contextId,
                agentName,
                new ProjectContextUsage
                {
                    InputTokenCount = usage.InputTokenCount ?? 0,
                    OutputTokenCount = usage.OutputTokenCount ?? 0,
                    TotalTokenCount = usage.TotalTokenCount ?? 0,
                    CachedInputTokenCount = usage.CachedInputTokenCount ?? 0,
                    ReasoningTokenCount = usage.ReasoningTokenCount ?? 0
                },
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to record agent usage for project {ProjectId} and context {ContextId}.",
                projectId,
                contextId);
        }
    }

    private static string ResolveAgentName(AIAgent agent) =>
        string.IsNullOrWhiteSpace(agent.Name) ? UnknownAgentName : agent.Name;
}
