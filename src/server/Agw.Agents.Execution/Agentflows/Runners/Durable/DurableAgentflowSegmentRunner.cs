using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Agentflows.Checkpoints.Durable;
using Agw.Agents.Execution.Agentflows.Context;
using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Durable;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using static Agw.Agents.Execution.Agentflows.Checkpoints.AgentflowCheckpointSupport;
using static Agw.Agents.Execution.Agentflows.Messaging.AgentflowMessageMapper;

namespace Agw.Agents.Execution.Agentflows.Runners.Durable;

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

    internal MafPermissionState CreatePermissionState(DurableExecutionManifest manifest) =>
        new(
            _humanInteractionContextAccessor?.PermissionState
                ?? new InteractionPermissionState(
                    manifest.Settings.PermissionMode,
                    manifest.ExecutionId,
                    manifest.Settings.PermissionVersion
                )
        );

    internal async Task<DurableExecutionSegmentResult> RunAsync(
        DurableExecutionManifest manifest,
        DurableExecutionSegmentInput input,
        IExecutionMessageSink sink,
        AgentflowAgentSessionScope sessionScope,
        AgentflowWorkflowLease workflowLease,
        CancellationToken cancellationToken
    )
    {
        var registry = new InteractionRequestRegistry(
            input
                .InputCatalog.Concat(
                    input.ResolvedInteractions.Select(item => item.Request).OfType<UserInputInteraction>()
                )
                .DistinctBy(item => item.InteractionId)
        );
        var handler = new DurableInteractionHandler(
            sessionScope.PermissionState.Permissions,
            manifest.Settings.HumanInteractionPolicy,
            registry,
            _humanInteractionContextAccessor!.RefreshPermissionsAsync
        );
        using var interactionScope = _humanInteractionContextAccessor!.Push(
            new ResolvedHumanInteractionChannel(
                input
                    .ResolvedInputs.Concat(
                        input.ResolvedInteractions.Where(item => item.Request is UserInputInteraction)
                    )
                    .DistinctBy(item => item.Request.InteractionId)
                    .ToArray()
            ),
            registry,
            sessionScope.PermissionState.Permissions
        );
        using var inputs = new AgentflowInputMessages(sessionScope);
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
                item => item.Request.InteractionId,
                StringComparer.Ordinal
            );
            var consumed = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Dictionary<string, InteractionRequest>(StringComparer.Ordinal);
            var deliveredMessages = new HashSet<(string MessageId, AiRole Role, string? Author)>();
            var pendingCheckpointRequests = new Dictionary<string, PendingCheckpointRequest>(StringComparer.Ordinal);
            // Manifest 的 Marker 只用于新恢复分支的首段，后续 HITL 分段不能再次跳过。
            var resumedCheckpointNodeIds =
                input.SegmentIndex == 1 && manifest.ResumeCheckpointOccurrenceId.HasValue
                    ? manifest.ResumeCheckpointNodeIds.ToHashSet(StringComparer.Ordinal)
                    : [];
            await foreach (
                var evt in inputs
                    .ObserveAsync(run.WatchStreamAsync(cancellationToken), cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                switch (evt)
                {
                    case AgentflowInputEvent inputEvent:
                        await sink.WriteAsync(inputEvent.Message, cancellationToken).ConfigureAwait(false);
                        break;

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

                        var approvalRequest = CreateInteractionRequest(externalRequest, humanGateNodes, registry);
                        if (approvalRequest == null)
                        {
                            return CreateDurableFailure(
                                input,
                                $"External request '{externalRequest.RequestId}' is unsupported."
                            );
                        }

                        if (responses.TryGetValue(approvalRequest.InteractionId, out var resolved))
                        {
                            var response = InteractionRules.ValidateAndNormalize(
                                approvalRequest,
                                resolved.Response,
                                sessionScope.PermissionState.Current
                            );
                            if (
                                await SendDurableResponseAsync(
                                        run,
                                        externalRequest,
                                        approvalRequest,
                                        response,
                                        sink,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false)
                            )
                                return new DurableExecutionSegmentResult
                                {
                                    ExecutionId = input.ExecutionId,
                                    SegmentIndex = input.SegmentIndex,
                                    Status = DurableExecutionSegmentStatus.Completed,
                                };
                            consumed.Add(approvalRequest.InteractionId);
                            break;
                        }

                        InteractionResolution resolution;
                        try
                        {
                            resolution = await handler.ResolveAsync(approvalRequest, cancellationToken);
                        }
                        catch (AgwException exception)
                        {
                            return CreateDurableFailure(input, exception.Message);
                        }
                        if (resolution is InteractionResolution.Resolved automatic)
                        {
                            await SendDurableResponseAsync(
                                    run,
                                    externalRequest,
                                    approvalRequest,
                                    automatic.Response,
                                    sink,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            pending.TryAdd(approvalRequest.InteractionId, approvalRequest);
                        }
                        break;
                    }

                    case AgentResponseUpdateEvent updateEvent when updateEvent.Data is AgentResponseUpdate update:
                        foreach (var updateMessage in MapEvent(evt, deliveredMessages))
                        {
                            await sink.WriteAsync(updateMessage, cancellationToken).ConfigureAwait(false);
                        }
                        break;

                    case AgentResponseEvent responseEvent when responseEvent.Data is AgentResponse response:
                        foreach (var message in MapEvent(evt, deliveredMessages))
                        {
                            await sink.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                        }
                        break;

                    case WorkflowOutputEvent outputEvent:
                        foreach (var message in MapEvent(evt, deliveredMessages))
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
                                manifest.UserId,
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

                        var missingWaitingResponse = responses.Keys.FirstOrDefault(id => !consumed.Contains(id));
                        if (missingWaitingResponse is not null)
                        {
                            return CreateDurableFailure(
                                input,
                                $"Agentflow did not restore human request '{missingWaitingResponse}'."
                            );
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
                            InputCatalog = registry.Snapshot(),
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
                                manifest.UserId,
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
    private static async Task<bool> SendDurableResponseAsync(
        StreamingRun run,
        ExternalRequest externalRequest,
        InteractionRequest request,
        InteractionResponse response,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken
    )
    {
        if (
            externalRequest.TryGetDataAs<Microsoft.Extensions.AI.ToolApprovalRequestContent>(out var toolApproval)
            && toolApproval is not null
        )
        {
            await run.SendResponseAsync(
                    externalRequest.CreateResponse(MafApprovalAdapter.CreateWorkflowResponse(toolApproval, response))
                )
                .ConfigureAwait(false);
            return false;
        }

        var decision = (WorkflowGateDecision)response;
        if (!decision.Approved)
        {
            await sink.WriteAsync(
                    CreateHumanGateRejectedMessage((WorkflowGateInteraction)request, Guid.CreateVersion7().Normalize()),
                    cancellationToken
                )
                .ConfigureAwait(false);
            await run.CancelRunAsync().ConfigureAwait(false);
            return true;
        }
        await run.SendResponseAsync(
                externalRequest.CreateResponse(
                    CreateHumanGateResponseMessages(GetHumanGateMessages(externalRequest), decision)
                )
            )
            .ConfigureAwait(false);
        return false;
    }
}
