using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using static Agw.Agents.Execution.Agentflows.AgentflowCheckpointSupport;
using static Agw.Agents.Execution.Agentflows.AgentflowMessageMapper;

namespace Agw.Agents.Execution.Agentflows;

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
        IHumanGateApprovalHandler? humanGateApprovalHandler,
        string executionUserId,
        Guid? sourceExecutionId,
        AgentflowCheckpointRuntimeState? checkpointState,
        AgentflowCheckpointSnapshot? resumeCheckpoint,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
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
        var executorsWithUpdates = new HashSet<string>(StringComparer.Ordinal);
        var pendingCheckpointRequests = new Dictionary<string, PendingCheckpointRequest>(StringComparer.Ordinal);
        // 每次显式 InProcess 恢复都携带本次 occurrence 的 Marker。
        var resumedCheckpointNodeIds =
            resumeCheckpoint?.Markers.Select(item => item.NodeId).ToHashSet(StringComparer.Ordinal) ?? [];

        await foreach (var evt in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("WorkflowEvent Type {Type}", evt.GetType().Name);
            switch (evt)
            {
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

                    if (
                        externalRequest.TryGetDataAs(
                            out Microsoft.Extensions.AI.ToolApprovalRequestContent? toolApprovalRequest
                        )
                    )
                    {
                        if (humanGateApprovalHandler == null)
                        {
                            _logger.LogWarning(
                                "Tool approval {RequestId} has no active approval handler.",
                                externalRequest.RequestId
                            );
                            await run.CancelRunAsync();
                            yield return CreateToolApprovalUnavailableMessage(
                                toolApprovalRequest,
                                Guid.CreateVersion7().Normalize()
                            );
                            yield return TurnMessageFactory.CreateFinished();
                            yield break;
                        }

                        var toolApprovalGate = ToolApprovalSupport.CreateRequest(
                            toolApprovalRequest,
                            externalRequest.PortInfo.PortId
                        );
                        if (humanGateApprovalHandler.RequiresHumanResponse(toolApprovalGate))
                        {
                            yield return ToolApprovalSupport.CreateMessage(toolApprovalGate);
                        }

                        var toolDecision = await humanGateApprovalHandler.WaitForApprovalAsync(
                            toolApprovalGate,
                            cancellationToken
                        );
                        var response = ToolApprovalSupport.CreateWorkflowResponse(toolApprovalRequest, toolDecision);
                        await run.SendResponseAsync(externalRequest.CreateResponse(response));
                        break;
                    }

                    if (!humanGateNodes.TryGetValue(externalRequest.PortInfo.PortId, out var humanGateNode))
                    {
                        break;
                    }

                    var approvalRequest = CreateHumanGateApprovalRequest(externalRequest, humanGateNode);
                    using var humanGateActivity = AgentflowNodeExecutionActivity.StartHumanGate(
                        executionTraceContext,
                        agentflowId,
                        humanGateNode.NodeId,
                        humanGateNode.Name,
                        approvalRequest.Messages
                    );

                    if (humanGateApprovalHandler == null)
                    {
                        humanGateActivity.Fail(
                            "HumanGateApprovalHandlerUnavailable: No approval handler was provided."
                        );
                        _logger.LogWarning(
                            "HumanGate {PortId} requested approval but no approval handler was provided.",
                            externalRequest.PortInfo.PortId
                        );
                        await run.CancelRunAsync();
                        yield return CreateHumanGateUnavailableMessage(
                            humanGateNode,
                            Guid.CreateVersion7().Normalize()
                        );
                        yield return TurnMessageFactory.CreateFinished();
                        yield break;
                    }

                    var approvalTask = humanGateApprovalHandler
                        .WaitForApprovalAsync(approvalRequest, cancellationToken)
                        .AsTask();

                    yield return CreateHumanGateApprovalRequestMessage(
                        approvalRequest,
                        Guid.CreateVersion7().Normalize()
                    );

                    HumanGateApprovalDecision decision;
                    try
                    {
                        decision = await approvalTask;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        humanGateActivity.Cancel();
                        await run.CancelRunAsync();
                        yield break;
                    }
                    catch (Exception exception)
                    {
                        humanGateActivity.Fail(exception);
                        throw;
                    }

                    if (!decision.Approved)
                    {
                        humanGateActivity.Reject();
                        await run.CancelRunAsync();
                        yield return CreateHumanGateRejectedMessage(approvalRequest, Guid.CreateVersion7().Normalize());
                        yield return TurnMessageFactory.CreateFinished();
                        yield break;
                    }

                    var responseMessages = CreateHumanGateResponseMessages(approvalRequest.Messages, decision);
                    try
                    {
                        await run.SendResponseAsync(externalRequest.CreateResponse(responseMessages));
                        humanGateActivity.Complete();
                    }
                    catch (OperationCanceledException)
                    {
                        humanGateActivity.Cancel();
                        throw;
                    }
                    catch (Exception exception)
                    {
                        humanGateActivity.Fail(exception);
                        throw;
                    }

                    break;
                }

                case AgentResponseUpdateEvent updateEvt when updateEvt.Data is AgentResponseUpdate update:
                    _logger.LogInformation(
                        "AgentResponseUpdateEvent {ExecutorId}, {Data}",
                        updateEvt.ExecutorId,
                        updateEvt.Data
                    );
                    executorsWithUpdates.Add(updateEvt.ExecutorId);
                    foreach (var chatMsg in MapEvent(evt))
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
                    if (executorsWithUpdates.Contains(responseEvt.ExecutorId))
                    {
                        break;
                    }

                    foreach (var responseMsg in MapEvent(evt))
                    {
                        yield return responseMsg;
                    }

                    break;

                case WorkflowOutputEvent outputEvt:
                    _logger.LogInformation("Workflow output: {Data}", outputEvt.Data);
                    foreach (var outputMessage in MapEvent(evt))
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

        yield return TurnMessageFactory.CreateFinished();
    }

    internal async Task<AgentflowExecutionResult> ExecuteAsync(
        Guid agentflowId,
        Guid taskId,
        string contextId,
        AgentflowWorkflowLease workflowLease,
        List<ChatMessage> messages,
        CancellationToken cancellationToken
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

                await run.CancelRunAsync();
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

                throw new AgwException(
                    ErrorCodes.AgentExecutionFailed,
                    $"Tool approval '{toolApprovalRequest.RequestId}' cannot be requested during unattended Agentflow execution."
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
