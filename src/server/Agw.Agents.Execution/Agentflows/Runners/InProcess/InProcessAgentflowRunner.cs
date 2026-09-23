using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Agentflows.Context;
using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agentflows.Runtime;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using static Agw.Agents.Execution.Agentflows.Checkpoints.AgentflowCheckpointSupport;
using static Agw.Agents.Execution.Agentflows.Messaging.AgentflowMessageMapper;

namespace Agw.Agents.Execution.Agentflows.Runners.InProcess;

/// <summary>
/// 消费 InProcess Workflow 事件，保留流式与非交互执行各自的终止语义。
/// </summary>
public sealed class InProcessAgentflowRunner
{
    private readonly ILogger<AgentflowRuntimeService> _logger;
    private readonly AgentflowExecutionContextFactory _executionContextFactory;
    private readonly AgentflowCheckpointSupport _checkpointSupport;

    public InProcessAgentflowRunner(
        ILogger<AgentflowRuntimeService> logger,
        AgentflowExecutionContextFactory executionContextFactory,
        AgentflowCheckpointSupport checkpointSupport
    )
    {
        _logger = logger;
        _executionContextFactory = executionContextFactory;
        _checkpointSupport = checkpointSupport;
    }

    internal async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        Guid agentflowId,
        AgwUserInput input,
        AgentflowAgentSessionScope sessionScope,
        AgentflowExecutionTraceContext executionTraceContext,
        AgentflowWorkflowLease workflowLease,
        IInteractionHandler? interactionHandler,
        string executionUserId,
        Guid? sourceExecutionId,
        AgentflowCheckpointRuntimeState? checkpointState,
        AgentflowCheckpointSnapshot? resumeCheckpoint,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var inputs = new AgentflowInputMessages(sessionScope);
        var workflow = workflowLease.Workflow;
        var checkpointNodeNames = workflowLease.Metadata.CheckpointNodes;

        var humanGateNodes = workflowLease.Metadata.HumanGateNodes;

        var mermaidString = WorkflowVisualizer.ToMermaidString(workflow);
        _logger.LogInformation("Constructed workflow: {Workflow}", mermaidString);

        var messages =
            resumeCheckpoint == null
                ? await _executionContextFactory
                    .CreateWorkflowInputMessagesAsync(
                        agentflowId,
                        sessionScope.ConversationId,
                        input,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
                : [];
        var checkpointedRun = await StartCheckpointedRunAsync(
                workflow,
                messages,
                agentflowId,
                executionTraceContext.TaskId,
                resumeCheckpoint?.Checkpoint,
                cancellationToken
            )
            .ConfigureAwait(false);
        await using var run = checkpointedRun.Run;
        var definitionFingerprint =
            checkpointState != null && _checkpointSupport.IsAvailable && sessionScope.ConversationId != Guid.Empty
                ? await _checkpointSupport
                    .GetDefinitionFingerprintAsync(agentflowId, cancellationToken)
                    .ConfigureAwait(false)
                : null;
        // Checkpoint 已包含未完成 turn 的消息，恢复不能再触发一次入口执行。
        if (resumeCheckpoint == null)
        {
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        }
        var deliveredMessages = new HashSet<(string ExecutorId, string MessageId, AiRole Role, string? Author)>();
        var executorsWithUpdates = new HashSet<string>(StringComparer.Ordinal);
        var pendingCheckpointRequests = new Dictionary<string, PendingCheckpointRequest>(StringComparer.Ordinal);
        // 每次显式 InProcess 恢复都携带本次 occurrence 的 Marker。
        var resumedCheckpointNodeIds =
            resumeCheckpoint?.Markers.Select(item => item.NodeId).ToHashSet(StringComparer.Ordinal) ?? [];

        var executionCancellationToken = cancellationToken;
        using var interactionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = interactionCancellation.Token;
        var pendingInteractions = new List<Task<AgwMessage?>>();
        await using var events = inputs
            .ObserveAsync(run.WatchStreamAsync(cancellationToken), cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        Task<bool>? nextEvent = null;
        try
        {
            while (true)
            {
                nextEvent ??= events.MoveNextAsync().AsTask();
                if (pendingInteractions.Count > 0)
                {
                    var ready = await Task.WhenAny(pendingInteractions.Cast<Task>().Append(nextEvent))
                        .ConfigureAwait(false);
                    if (ready != nextEvent)
                    {
                        var resolved = (Task<AgwMessage?>)ready;
                        pendingInteractions.Remove(resolved);
                        if (resolved.IsCanceled && cancellationToken.IsCancellationRequested)
                        {
                            await run.CancelRunAsync().ConfigureAwait(false);
                            yield break;
                        }
                        if (await resolved.ConfigureAwait(false) is { } rejection)
                        {
                            await run.CancelRunAsync().ConfigureAwait(false);
                            yield return rejection;
                            yield return TurnMessageFactory.CreateFinished();
                            yield break;
                        }
                        continue;
                    }
                }
                bool available;
                try
                {
                    available = await nextEvent.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    available = false;
                }
                if (!available)
                    break;
                var evt = events.Current;
                nextEvent = null;
                _logger.LogInformation("WorkflowEvent Type {Type}", evt.GetType().Name);
                switch (evt)
                {
                    case AgentflowInputEvent inputEvent:
                        yield return inputEvent.Message;
                        break;

                    case ExecutorInvokedEvent invoke:
                        _logger.LogInformation("Starting {ExecutorId}", invoke.ExecutorId);
                        break;

                    case ExecutorCompletedEvent complete:
                        _logger.LogInformation("Completed {ExecutorId}, {Data}", complete.ExecutorId, complete.Data);
                        break;

                    case RequestInfoEvent requestInfo:
                    {
                        var externalRequest = requestInfo.Request;
                        _logger.LogInformation(
                            "External request {RequestId} from port {PortId}",
                            externalRequest.RequestId,
                            externalRequest.PortInfo.PortId
                        );

                        if (TryCreateCheckpointRequest(externalRequest, checkpointNodeNames, out var checkpointRequest))
                        {
                            if (resumedCheckpointNodeIds.Remove(checkpointRequest.NodeId))
                            {
                                await ContinueCheckpointRequestsAsync(run, [checkpointRequest], cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                pendingCheckpointRequests[externalRequest.RequestId] = checkpointRequest;
                            }
                            break;
                        }

                        var request = CreateInteractionRequest(
                            externalRequest,
                            humanGateNodes,
                            interactionHandler?.Requests
                        );
                        if (request is null)
                            break;
                        if (interactionHandler is null)
                        {
                            await run.CancelRunAsync().ConfigureAwait(false);
                            yield return request is WorkflowGateInteraction gate
                                ? CreateHumanGateUnavailableMessage(
                                    humanGateNodes[gate.Source.NodeId!],
                                    Guid.CreateVersion7().Normalize()
                                )
                                : CreateToolApprovalUnavailableMessage(
                                    GetToolApproval(externalRequest),
                                    Guid.CreateVersion7().Normalize()
                                );
                            yield return TurnMessageFactory.CreateFinished();
                            yield break;
                        }
                        // Keep consuming workflow events while each independent request awaits its response.
                        pendingInteractions.Add(
                            ResolveWorkflowInteractionAsync(
                                run,
                                externalRequest,
                                request,
                                interactionHandler,
                                executionTraceContext,
                                agentflowId,
                                cancellationToken
                            )
                        );
                        break;
                    }

                    case AgentResponseUpdateEvent updateEvt when updateEvt.Data is AgentResponseUpdate update:
                        executorsWithUpdates.Add(updateEvt.ExecutorId);
                        _logger.LogInformation(
                            "AgentResponseUpdateEvent {ExecutorId}, {Data}",
                            updateEvt.ExecutorId,
                            updateEvt.Data
                        );
                        foreach (var chatMsg in MapEvent(evt, deliveredMessages))
                        {
                            yield return chatMsg;
                        }

                        break;

                    case AgentResponseEvent responseEvt when responseEvt.Data is AgentResponse response:
                        _logger.LogInformation(
                            "AgentResponseEvent {ExecutorId}, {Data}",
                            responseEvt.ExecutorId,
                            responseEvt.Data
                        );
                        if (
                            executorsWithUpdates.Contains(responseEvt.ExecutorId)
                            && response.Messages.All(message =>
                                message.Contents.All(content => content is ToolApprovalRequestContent)
                            )
                        )
                            break;
                        foreach (var responseMsg in MapEvent(evt, deliveredMessages))
                        {
                            yield return responseMsg;
                        }

                        break;

                    case WorkflowOutputEvent outputEvt:
                        _logger.LogInformation("Workflow output: {Data}", outputEvt.Data);
                        foreach (var outputMessage in MapEvent(evt, deliveredMessages))
                        {
                            yield return outputMessage;
                        }

                        break;

                    case SuperStepCompletedEvent completed:
                        var checkpointMarkers = CreateCheckpointMarkers(pendingCheckpointRequests.Values);
                        var recorded = await _checkpointSupport
                            .RecordCheckpointAsync(
                                sourceExecutionId,
                                sessionScope.ProjectId,
                                sessionScope.ConversationId,
                                executionTraceContext.ContextId,
                                executionTraceContext.TaskId,
                                agentflowId,
                                executionUserId,
                                isDurable: false,
                                definitionFingerprint,
                                checkpointedRun.Store,
                                completed.CompletionInfo?.Checkpoint,
                                checkpointMarkers,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                        if (recorded != null)
                        {
                            checkpointState?.Register(recorded.Snapshot);
                            foreach (var checkpointMessage in recorded.Messages)
                            {
                                yield return checkpointMessage;
                            }
                        }
                        _checkpointSupport.LogCheckpoint(completed, checkpointMarkers);
                        await ContinueCheckpointRequestsAsync(run, pendingCheckpointRequests.Values, cancellationToken)
                            .ConfigureAwait(false);
                        pendingCheckpointRequests.Clear();
                        break;

                    case WorkflowErrorEvent error:
                        _logger.LogError(error.Exception, "Workflow error");
                        yield return CreateWorkflowErrorMessage(error.Exception, Guid.CreateVersion7().Normalize());
                        yield return TurnMessageFactory.CreateFinished();
                        yield break;
                }
            }
        }
        finally
        {
            await interactionCancellation.CancelAsync().ConfigureAwait(false);
            await ((Task)Task.WhenAll(pendingInteractions)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (nextEvent is not null)
                await ((Task)nextEvent).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        if (!executionCancellationToken.IsCancellationRequested)
            yield return TurnMessageFactory.CreateFinished();
    }

    private static ToolApprovalRequestContent GetToolApproval(ExternalRequest request)
    {
        request.TryGetDataAs<ToolApprovalRequestContent>(out var approval);
        return approval!;
    }

    private static async Task<AgwMessage?> ResolveWorkflowInteractionAsync(
        StreamingRun run,
        ExternalRequest externalRequest,
        InteractionRequest request,
        IInteractionHandler handler,
        AgentflowExecutionTraceContext trace,
        Guid agentflowId,
        CancellationToken cancellationToken
    )
    {
        using var activity = request is WorkflowGateInteraction gate
            ? AgentflowNodeExecutionActivity.StartHumanGate(
                trace,
                agentflowId,
                gate.Source.NodeId!,
                gate.Source.NodeName,
                GetHumanGateMessages(externalRequest)
            )
            : null;
        try
        {
            var response = InteractionResults.RequireResolved(
                await handler.ResolveAsync(request, cancellationToken).ConfigureAwait(false)
            );
            if (response is WorkflowGateDecision decision)
            {
                if (!decision.Approved)
                {
                    activity?.Reject();
                    return CreateHumanGateRejectedMessage(
                        (WorkflowGateInteraction)request,
                        Guid.CreateVersion7().Normalize()
                    );
                }
                await run.SendResponseAsync(
                        externalRequest.CreateResponse(
                            CreateHumanGateResponseMessages(GetHumanGateMessages(externalRequest), decision)
                        )
                    )
                    .ConfigureAwait(false);
            }
            else
            {
                await run.SendResponseAsync(
                        externalRequest.CreateResponse(
                            MafApprovalAdapter.CreateWorkflowResponse(GetToolApproval(externalRequest), response)
                        )
                    )
                    .ConfigureAwait(false);
            }
            activity?.Complete();
            return null;
        }
        catch (OperationCanceledException)
        {
            activity?.Cancel();
            throw;
        }
        catch (Exception exception)
        {
            activity?.Fail(exception);
            throw;
        }
    }

    internal async Task<AgentflowExecutionResult> ExecuteAsync(
        Guid agentflowId,
        Guid taskId,
        string contextId,
        AgentflowWorkflowLease workflowLease,
        List<ChatMessage> messages,
        CancellationToken cancellationToken,
        IInteractionHandler? approvalHandler = null
    )
    {
        var workflow = workflowLease.Workflow;
        var checkpointNodeNames = workflowLease.Metadata.CheckpointNodes;

        var checkpointedRun = await StartCheckpointedRunAsync(
                workflow,
                messages,
                agentflowId,
                taskId,
                resumeCheckpoint: null,
                cancellationToken
            )
            .ConfigureAwait(false);
        await using var run = checkpointedRun.Run;
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var outputs = new List<AgwMessage>();
        var pendingCheckpointRequests = new Dictionary<string, PendingCheckpointRequest>(StringComparer.Ordinal);
        await foreach (var evt in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            if (evt is RequestInfoEvent requestInfo)
            {
                if (TryCreateCheckpointRequest(requestInfo.Request, checkpointNodeNames, out var checkpointRequest))
                {
                    pendingCheckpointRequests[requestInfo.Request.RequestId] = checkpointRequest;
                    continue;
                }

                if (
                    !requestInfo.Request.TryGetDataAs(
                        out Microsoft.Extensions.AI.ToolApprovalRequestContent? toolApprovalRequest
                    )
                )
                {
                    throw new AgwException(
                        ErrorCodes.AgentExecutionFailed,
                        $"External request '{requestInfo.Request.RequestId}' cannot be handled during unattended Agentflow execution."
                    );
                }

                var request = MafApprovalAdapter.CreateRequest(
                    toolApprovalRequest,
                    requestInfo.Request.PortInfo.PortId
                );
                var decision = InteractionResults.RequireResolved(
                    await (approvalHandler ?? new UnattendedInteractionHandler(null)).ResolveAsync(
                        request,
                        cancellationToken
                    )
                );
                await run.SendResponseAsync(
                    requestInfo.Request.CreateResponse(
                        MafApprovalAdapter.CreateWorkflowResponse(toolApprovalRequest, decision)
                    )
                );
            }
            else if (evt is AgentResponseUpdateEvent updateEvt)
            {
                _logger.LogDebug("{ExecutorId}: {Data}", updateEvt.ExecutorId, updateEvt.Data);
            }
            else if (evt is WorkflowOutputEvent outputEvt)
            {
                outputs.AddRange(MapEvent(evt));
            }
            else if (evt is SuperStepCompletedEvent completed)
            {
                var checkpointMarkers = CreateCheckpointMarkers(pendingCheckpointRequests.Values);
                _checkpointSupport.LogCheckpoint(completed, checkpointMarkers);
                await ContinueCheckpointRequestsAsync(run, pendingCheckpointRequests.Values, cancellationToken)
                    .ConfigureAwait(false);
                pendingCheckpointRequests.Clear();
            }
        }

        var taskIdString = taskId.Normalize();

        return new AgentflowExecutionResult(taskIdString, contextId, outputs);
    }
}
