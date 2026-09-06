using Agw.Agents.Execution.Durable;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using static Agw.Agents.Execution.Agentflows.AgentflowCheckpointSupport;
using static Agw.Agents.Execution.Agentflows.AgentflowMessageMapper;

namespace Agw.Agents.Execution.Agentflows;

/// <summary>
/// 执行一个可恢复分段；Workflow 资源由调用方的 Lease 管理。
/// </summary>
public sealed class DurableAgentflowSegmentRunner
{
    private readonly AgentflowExecutionContextFactory _executionContextFactory;
    private readonly AgentflowCheckpointSupport _checkpointSupport;
    private readonly HumanInteractionContextAccessor? _humanInteractionContextAccessor;

    public DurableAgentflowSegmentRunner(
        AgentflowExecutionContextFactory executionContextFactory,
        AgentflowCheckpointSupport checkpointSupport,
        HumanInteractionContextAccessor? humanInteractionContextAccessor = null
    )
    {
        _executionContextFactory = executionContextFactory;
        _checkpointSupport = checkpointSupport;
        _humanInteractionContextAccessor = humanInteractionContextAccessor;
    }

    internal bool IsAvailable => _humanInteractionContextAccessor != null;

    internal async Task<DurableExecutionSegmentResult> RunAsync(
        DurableExecutionManifest manifest,
        DurableExecutionSegmentInput input,
        IExecutionMessageSink sink,
        AgentflowAgentSessionScope sessionScope,
        AgentflowWorkflowLease workflowLease,
        CancellationToken cancellationToken
    )
    {
        using var interactionScope = _humanInteractionContextAccessor!.Push(
            new ResolvedHumanInteractionChannel(input.ResolvedInteractions)
        );
        var workflow = workflowLease.Workflow;
        var humanGateNodes = workflowLease.Metadata.HumanGateNodes;
        var checkpointNodeNames = workflowLease.Metadata.CheckpointNodes;
        var sessionId = input.Checkpoint?.SessionId ?? $"durable-{manifest.ExecutionId:N}";
        var checkpointStore = new DurableAgentflowCheckpointStore(input.Checkpoint);
        var checkpointManager = CheckpointManager.CreateJson(checkpointStore);
        var definitionFingerprint = !_checkpointSupport.IsAvailable
            ? null
            : await _checkpointSupport
                .GetDefinitionFingerprintAsync(manifest.AgentId, cancellationToken)
                .ConfigureAwait(false);
        StreamingRun run;
        if (input.SegmentIndex == 0)
        {
            var messages = await _executionContextFactory
                .CreateWorkflowInputMessagesAsync(
                    manifest.AgentId,
                    manifest.Task.ProjectConversationId,
                    manifest.Input,
                    cancellationToken
                )
                .ConfigureAwait(false);
            run = await InProcessExecution.RunStreamingAsync(
                workflow,
                messages,
                checkpointManager,
                sessionId,
                cancellationToken
            );
        }
        else
        {
            var checkpoint = await checkpointManager
                .GetLatestCheckpointAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (checkpoint == null)
            {
                return CreateDurableFailure(input, "Agentflow checkpoint could not be found.");
            }

            run = await InProcessExecution.ResumeStreamingAsync(
                workflow,
                checkpoint,
                checkpointManager,
                cancellationToken
            );
        }

        await using (run)
        {
            // 恢复会重新发布待处理请求并恢复队列；只有首次执行需要启动新 turn。
            if (input.SegmentIndex == 0)
            {
                await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            }
            var responses = input.ResolvedInteractions.ToDictionary(
                item => item.Request.RequestId,
                StringComparer.Ordinal
            );
            var consumed = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Dictionary<string, DurableHumanInteractionSnapshot>(StringComparer.Ordinal);
            var executorsWithUpdates = new HashSet<string>(StringComparer.Ordinal);
            var pendingCheckpointRequests = new Dictionary<string, PendingCheckpointRequest>(StringComparer.Ordinal);
            // Manifest 的 Marker 只用于新恢复分支的首段，后续 HITL 分段不能再次跳过。
            var resumedCheckpointNodeIds =
                input.SegmentIndex == 1 && manifest.ResumeCheckpointOccurrenceId.HasValue
                    ? manifest.ResumeCheckpointNodeIds.ToHashSet(StringComparer.Ordinal)
                    : [];
            await foreach (var evt in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case RequestInfoEvent requestInfo:
                    {
                        var externalRequest = requestInfo.Request;
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

                        var approvalRequest = CreateDurableApprovalRequest(externalRequest, humanGateNodes);
                        if (approvalRequest == null)
                        {
                            return CreateDurableFailure(
                                input,
                                $"External request '{externalRequest.RequestId}' is unsupported."
                            );
                        }

                        if (responses.TryGetValue(approvalRequest.RequestId, out var resolved))
                        {
                            await SendDurableResponseAsync(
                                    run,
                                    externalRequest,
                                    approvalRequest,
                                    resolved.Response,
                                    sink,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            consumed.Add(approvalRequest.RequestId);
                            break;
                        }

                        pending.TryAdd(
                            approvalRequest.RequestId,
                            DurableHumanInteractionMapper.FromRequest(approvalRequest)
                        );
                        break;
                    }

                    case AgentResponseUpdateEvent updateEvent when updateEvent.Data is AgentResponseUpdate update:
                        executorsWithUpdates.Add(updateEvent.ExecutorId);
                        foreach (var updateMessage in MapEvent(evt))
                        {
                            await sink.WriteAsync(updateMessage, cancellationToken).ConfigureAwait(false);
                        }
                        break;

                    case AgentResponseEvent responseEvent when responseEvent.Data is AgentResponse response:
                        if (!executorsWithUpdates.Contains(responseEvent.ExecutorId))
                        {
                            foreach (var message in MapEvent(evt))
                            {
                                await sink.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        break;

                    case WorkflowOutputEvent outputEvent:
                        foreach (var message in MapEvent(evt))
                        {
                            await sink.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                        }
                        break;

                    case WorkflowErrorEvent error:
                        await sink.WriteAsync(
                                CreateWorkflowErrorMessage(error.Exception, Guid.CreateVersion7().Normalize()),
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                        return CreateDurableFailure(input, error.Exception?.Message ?? "Agentflow execution failed.");

                    case SuperStepCompletedEvent completed
                        when completed.CompletionInfo is { HasPendingRequests: true } completion && pending.Count > 0:
                        var waitingCheckpointMarkers = CreateCheckpointMarkers(pendingCheckpointRequests.Values);
                        var waitingCheckpoint = await _checkpointSupport
                            .RecordCheckpointAsync(
                                input.ExecutionId,
                                sessionScope.ProjectId,
                                sessionScope.ConversationId,
                                sessionScope.ContextId,
                                manifest.Task.TaskId,
                                manifest.AgentId,
                                manifest.ResolveUserId(),
                                isDurable: true,
                                definitionFingerprint,
                                checkpointStore,
                                completion.Checkpoint,
                                waitingCheckpointMarkers,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                        if (waitingCheckpoint != null)
                        {
                            foreach (var checkpointMessage in waitingCheckpoint.Messages)
                            {
                                await sink.WriteAsync(checkpointMessage, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        _checkpointSupport.LogCheckpoint(completed, waitingCheckpointMarkers);
                        if (completion.Checkpoint == null)
                        {
                            return CreateDurableFailure(
                                input,
                                "Agentflow reached a human interaction without a checkpoint."
                            );
                        }

                        if (pendingCheckpointRequests.Count > 0)
                        {
                            await ContinueCheckpointRequestsAsync(
                                    run,
                                    pendingCheckpointRequests.Values,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            pendingCheckpointRequests.Clear();
                            break;
                        }

                        await run.CancelRunAsync().ConfigureAwait(false);
                        var durableCheckpoint = checkpointStore.Latest;
                        if (durableCheckpoint == null)
                        {
                            return CreateDurableFailure(input, "Agentflow checkpoint was not persisted.");
                        }

                        return new DurableExecutionSegmentResult
                        {
                            ExecutionId = input.ExecutionId,
                            SegmentIndex = input.SegmentIndex,
                            Status = DurableExecutionSegmentStatus.WaitingForHuman,
                            PendingInteractions = pending.Values.ToArray(),
                            Checkpoint = durableCheckpoint,
                        };

                    case SuperStepCompletedEvent completed:
                        var checkpointMarkers = CreateCheckpointMarkers(pendingCheckpointRequests.Values);
                        var recordedCheckpoint = await _checkpointSupport
                            .RecordCheckpointAsync(
                                input.ExecutionId,
                                sessionScope.ProjectId,
                                sessionScope.ConversationId,
                                sessionScope.ContextId,
                                manifest.Task.TaskId,
                                manifest.AgentId,
                                manifest.ResolveUserId(),
                                isDurable: true,
                                definitionFingerprint,
                                checkpointStore,
                                completed.CompletionInfo?.Checkpoint,
                                checkpointMarkers,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                        if (recordedCheckpoint != null)
                        {
                            foreach (var checkpointMessage in recordedCheckpoint.Messages)
                            {
                                await sink.WriteAsync(checkpointMessage, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        _checkpointSupport.LogCheckpoint(completed, checkpointMarkers);
                        await ContinueCheckpointRequestsAsync(run, pendingCheckpointRequests.Values, cancellationToken)
                            .ConfigureAwait(false);
                        pendingCheckpointRequests.Clear();
                        break;
                }
            }

            var missingResponses = responses.Keys.Except(consumed, StringComparer.Ordinal).ToArray();
            if (missingResponses.Length > 0)
            {
                return CreateDurableFailure(input, $"Agentflow did not restore human request '{missingResponses[0]}'.");
            }

            return new DurableExecutionSegmentResult
            {
                ExecutionId = input.ExecutionId,
                SegmentIndex = input.SegmentIndex,
                Status = DurableExecutionSegmentStatus.Completed,
            };
        }
    }

    /// <summary>
    /// 把 PostgreSQL 中持久化的人工回答发送给恢复后的 Agentflow external request。
    /// </summary>
    private static async Task SendDurableResponseAsync(
        StreamingRun run,
        ExternalRequest externalRequest,
        HumanGateApprovalRequest request,
        DurableHumanResponseEnvelope response,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken
    )
    {
        if (request.ToolApprovalRequest is { } toolApprovalRequest)
        {
            var decision = new HumanGateApprovalDecision(
                response.RequestId,
                response.Approved,
                response.ResponseText,
                response.ApprovalScope,
                response.ResponseData
            );
            await run.SendResponseAsync(
                    externalRequest.CreateResponse(
                        ToolApprovalSupport.CreateWorkflowResponse(toolApprovalRequest, decision)
                    )
                )
                .ConfigureAwait(false);
            return;
        }

        var humanDecision = new HumanGateApprovalDecision(
            response.RequestId,
            response.Approved,
            response.ResponseText,
            response.ApprovalScope,
            response.ResponseData
        );
        if (!response.Approved)
        {
            await sink.WriteAsync(
                    CreateHumanGateRejectedMessage(request, Guid.CreateVersion7().Normalize()),
                    cancellationToken
                )
                .ConfigureAwait(false);
            await run.CancelRunAsync().ConfigureAwait(false);
            return;
        }

        await run.SendResponseAsync(
                externalRequest.CreateResponse(CreateHumanGateResponseMessages(request.Messages, humanDecision))
            )
            .ConfigureAwait(false);
    }
}
