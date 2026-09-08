using Agw.Agents.Execution.Durable;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agentflows;

/// <summary>
/// 共享 Checkpoint 操作；待处理请求和恢复状态由每次 Runner 调用持有。
/// </summary>
public sealed class AgentflowCheckpointSupport
{
    private readonly ILogger<AgentflowRuntimeService> _logger;
    private readonly AgentflowCheckpointStore? _checkpointStore;

    public AgentflowCheckpointSupport(
        ILogger<AgentflowRuntimeService> logger,
        AgentflowCheckpointStore? checkpointStore = null
    )
    {
        _logger = logger;
        _checkpointStore = checkpointStore;
    }

    internal bool IsAvailable => _checkpointStore != null;

    internal Task<string?> GetDefinitionFingerprintAsync(Guid agentflowId, CancellationToken cancellationToken) =>
        _checkpointStore?.GetDefinitionFingerprintAsync(agentflowId, cancellationToken)
        ?? Task.FromResult<string?>(null);

    private static string CreateCheckpointSessionId(Guid agentflowId, Guid taskId) =>
        $"agentflow-{agentflowId:N}-task-{taskId:N}";

    internal static async ValueTask<CheckpointedStreamingRun> StartCheckpointedRunAsync(
        Workflow workflow,
        List<ChatMessage> messages,
        Guid agentflowId,
        Guid taskId,
        DurableAgentflowCheckpoint? resumeCheckpoint,
        CancellationToken cancellationToken
    )
    {
        var store = new DurableAgentflowCheckpointStore(resumeCheckpoint);
        var checkpointManager = CheckpointManager.CreateJson(store);
        if (resumeCheckpoint == null)
        {
            var run = await InProcessExecution
                .RunStreamingAsync(
                    workflow,
                    messages,
                    checkpointManager,
                    CreateCheckpointSessionId(agentflowId, taskId),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return new CheckpointedStreamingRun(run, store);
        }

        var checkpoint =
            await checkpointManager
                .GetLatestCheckpointAsync(resumeCheckpoint.SessionId, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.AgentExecutionFailed, "Agentflow checkpoint could not be restored.");
        var resumedRun = await InProcessExecution
            .ResumeStreamingAsync(workflow, checkpoint, checkpointManager, cancellationToken)
            .ConfigureAwait(false);
        return new CheckpointedStreamingRun(resumedRun, store);
    }

    internal async Task<RecordedAgentflowCheckpoint?> RecordCheckpointAsync(
        Guid? sourceExecutionId,
        Guid projectId,
        Guid conversationId,
        string contextId,
        Guid taskId,
        Guid agentflowId,
        string userId,
        bool isDurable,
        string? definitionFingerprint,
        DurableAgentflowCheckpointStore runCheckpointStore,
        CheckpointInfo? frameworkCheckpoint,
        IReadOnlyDictionary<string, string> checkpointMarkers,
        CancellationToken cancellationToken
    )
    {
        if (
            _checkpointStore == null
            || conversationId == Guid.Empty
            || checkpointMarkers.Count == 0
            || frameworkCheckpoint == null
            || string.IsNullOrWhiteSpace(definitionFingerprint)
        )
        {
            return null;
        }

        var checkpoint = runCheckpointStore.Latest;
        if (
            checkpoint == null
            || !string.Equals(checkpoint.SessionId, frameworkCheckpoint.SessionId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.CheckpointId, frameworkCheckpoint.CheckpointId, StringComparison.Ordinal)
        )
        {
            _logger.LogWarning("Agentflow checkpoint markers were emitted without a persisted framework checkpoint.");
            return null;
        }

        return await _checkpointStore
            .RecordAsync(
                sourceExecutionId,
                projectId,
                conversationId,
                contextId,
                taskId,
                agentflowId,
                userId,
                isDurable,
                definitionFingerprint,
                checkpoint,
                checkpointMarkers,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    internal sealed record CheckpointedStreamingRun(StreamingRun Run, DurableAgentflowCheckpointStore Store);

    internal static bool TryCreateCheckpointRequest(
        ExternalRequest request,
        IReadOnlyDictionary<string, CheckpointRequestNode> checkpointNodes,
        out PendingCheckpointRequest checkpointRequest
    )
    {
        if (!checkpointNodes.TryGetValue(request.PortInfo.PortId, out var checkpointNode))
        {
            checkpointRequest = null!;
            return false;
        }

        var messages =
            request.TryGetDataAs<List<ChatMessage>>(out var requestedMessages) && requestedMessages != null
                ? requestedMessages
                : [];
        checkpointRequest = new PendingCheckpointRequest(request, checkpointNode.NodeId, checkpointNode.Name, messages);
        return true;
    }

    internal static IReadOnlyDictionary<string, string> CreateCheckpointMarkers(
        IEnumerable<PendingCheckpointRequest> requests
    ) =>
        requests
            .GroupBy(item => item.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().CheckpointName, StringComparer.Ordinal);

    internal static async Task ContinueCheckpointRequestsAsync(
        StreamingRun run,
        IEnumerable<PendingCheckpointRequest> requests,
        CancellationToken cancellationToken
    )
    {
        foreach (var request in requests)
        {
            await run.SendResponseAsync(request.Request.CreateResponse(request.Messages)).ConfigureAwait(false);
        }
    }

    internal sealed record PendingCheckpointRequest(
        ExternalRequest Request,
        string NodeId,
        string CheckpointName,
        List<ChatMessage> Messages
    );

    internal void LogCheckpoint(
        SuperStepCompletedEvent completed,
        IReadOnlyDictionary<string, string> checkpointMarkers
    )
    {
        var completion = completed.CompletionInfo;
        var checkpoint = completion?.Checkpoint;
        if (completion == null || checkpoint == null)
        {
            return;
        }

        foreach (var (nodeId, checkpointName) in checkpointMarkers)
        {
            _logger.LogInformation(
                "Agentflow named checkpoint {CheckpointName} ({CheckpointNodeId}) created as {CheckpointId} for session {CheckpointSessionId} at superstep {SuperStep}",
                checkpointName,
                nodeId,
                checkpoint.CheckpointId,
                checkpoint.SessionId,
                completed.StepNumber
            );
        }
    }
}
