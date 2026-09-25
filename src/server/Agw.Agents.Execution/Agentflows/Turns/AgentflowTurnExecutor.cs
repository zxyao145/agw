using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agentflows.Runtime;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Contracts.History;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using static Agw.Agents.Execution.Agentflows.Checkpoints.AgentflowCheckpointSupport;
using static Agw.Agents.Execution.Agentflows.Messaging.AgentflowMessageMapper;

namespace Agw.Agents.Execution.Agentflows.Turns;

/// <summary>
/// 用一个 AgentflowRuntime 跑一个 Turn 或它的一个 Segment：消费 Workflow 事件，交给决定来源处理请求，在 Superstep 边界记录存档。
/// Runs one turn, or one of its segments, with an AgentflowRuntime: consumes Workflow events, hands requests to the decision source and records checkpoints at Superstep boundaries.
/// </summary>
/// <remarks>
/// 决定来源在进程内等待回答时，事件消费不中断；决定来源返回等待边界时，本 Superstep 的存档完成后结束 Segment 并报告 WaitingForHuman。
/// While the decision source waits for answers in memory, event consumption continues; when it returns a wait boundary, the segment ends with WaitingForHuman after this Superstep's checkpoint.
/// </remarks>
public sealed class AgentflowTurnExecutor
{
    private readonly AgentflowRuntimeFactory _runtimeFactory;
    private readonly AgentflowCheckpointSupport _checkpointSupport;
    private readonly IConversationHistoryStore? _historyStore;
    private readonly ILogger<AgentflowTurnExecutor> _logger;

    public AgentflowTurnExecutor(
        AgentflowRuntimeFactory runtimeFactory,
        AgentflowCheckpointSupport checkpointSupport,
        ILogger<AgentflowTurnExecutor> logger,
        IConversationHistoryStore? historyStore = null
    )
    {
        _runtimeFactory = runtimeFactory;
        _checkpointSupport = checkpointSupport;
        _logger = logger;
        _historyStore = historyStore;
    }

    internal async IAsyncEnumerable<AgwMessage> RunAsync(
        ExecutionScope scope,
        AgentflowRuntime runtime,
        TurnInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(input);
        var resources = await _runtimeFactory.PrepareTurnAsync(runtime, scope, cancellationToken).ConfigureAwait(false);
        await foreach (
            var message in TurnHistory.RunAsync(
                scope,
                _historyStore,
                new ConversationHistoryScope
                {
                    ProjectId = resources.SessionScope.ProjectId,
                    ContextId = resources.SessionScope.ContextId,
                    Generation = scope.Generation,
                    IsExecutionBound = resources.SessionScope.ConversationId != Guid.Empty,
                },
                RunPreparedAsync(scope, runtime, resources, input, cancellationToken),
                cancellationToken
            )
        )
        {
            yield return message;
        }
    }

    /// <summary>
    /// 在历史作用域内编译 Workflow 并执行；节点资源在历史刷新前释放，外部 Engine 释放时提交的历史进入同一次刷新。
    /// Compiles and runs the Workflow inside the history scope; node resources are released before the flush so history committed on release joins it.
    /// </summary>
    private async IAsyncEnumerable<AgwMessage> RunPreparedAsync(
        ExecutionScope scope,
        AgentflowRuntime runtime,
        AgentflowTurnResources resources,
        TurnInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var workflowLease =
            await _runtimeFactory.CreateAiWorkflow(
                resources.Agentflow,
                cancellationToken,
                resources.SessionScope,
                resources.TraceContext,
                runtime.Settings.EnvironmentVariables,
                runtime.DeferHumanInteractions
            ) ?? throw new AgwException(ErrorCodes.AgentExecutionFailed, "Agentflow could not be constructed.");
        var run = new WorkflowRunState();
        await using (workflowLease)
        {
            await foreach (
                var message in TurnHistory
                    .ObserveAsync(
                        scope,
                        RunWorkflowAsync(scope, runtime, resources, workflowLease, input, run, cancellationToken),
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            )
            {
                yield return message;
            }
        }

        if (scope.History is { } history)
            await history.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        if (run.Outcome != null)
        {
            scope.ReportOutcome(run.Outcome with { StepCount = run.CompletedSupersteps });
        }
    }

    private async IAsyncEnumerable<AgwMessage> RunWorkflowAsync(
        ExecutionScope scope,
        AgentflowRuntime runtime,
        AgentflowTurnResources resources,
        AgentflowWorkflowLease workflowLease,
        TurnInput input,
        WorkflowRunState state,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var sessionScope = resources.SessionScope;
        var agentflowId = resources.Agentflow.Id;
        using var inputs = new AgentflowInputMessages(sessionScope);
        var workflow = workflowLease.Workflow;
        var checkpointNodeNames = workflowLease.Metadata.CheckpointNodes;
        var humanGateNodes = workflowLease.Metadata.HumanGateNodes;
        var handler = scope.InteractionHandler;
        var resume = input.Resume;
        _logger.LogInformation("Constructed workflow: {Workflow}", WorkflowVisualizer.ToMermaidString(workflow));

        var messages =
            resume?.Checkpoint == null
                ? await _runtimeFactory
                    .CreateWorkflowInputMessagesAsync(
                        agentflowId,
                        sessionScope.ConversationId,
                        input.UserInput,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
                : [];
        var checkpointedRun = await StartCheckpointedRunAsync(
                workflow,
                messages,
                agentflowId,
                scope.Context.TurnId,
                resume?.Checkpoint,
                cancellationToken
            )
            .ConfigureAwait(false);
        await using var run = checkpointedRun.Run;
        var definitionFingerprint =
            _checkpointSupport.IsAvailable && sessionScope.ConversationId != Guid.Empty
                ? await _checkpointSupport
                    .GetDefinitionFingerprintAsync(agentflowId, cancellationToken)
                    .ConfigureAwait(false)
                : null;
        // 存档已包含未完成 Turn 的队列，恢复时不能再触发一次入口执行。
        // The checkpoint already holds the unfinished turn's queue, so a resume must not trigger the entry again.
        if (resume?.Checkpoint == null)
        {
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        }
        var answers = (resume?.ResolvedInteractions ?? []).ToDictionary(
            item => item.Request.InteractionId,
            StringComparer.Ordinal
        );
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var waiting = new Dictionary<string, InteractionRequest>(StringComparer.Ordinal);
        var deliveredMessages = new HashSet<(string ExecutorId, string MessageId, AiRole Role, string? Author)>();
        var executorsWithUpdates = new HashSet<string>(StringComparer.Ordinal);
        var pendingCheckpointRequests = new Dictionary<string, PendingCheckpointRequest>(StringComparer.Ordinal);
        var resumedCheckpointNodeIds = new HashSet<string>(
            resume?.CheckpointNodeIds ?? new HashSet<string>(),
            StringComparer.Ordinal
        );

        using var interactionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var interactionToken = interactionCancellation.Token;
        var pendingInteractions = new List<Task<WorkflowInteractionResult>>();
        await using var events = inputs
            .ObserveAsync(run.WatchStreamAsync(interactionToken), interactionToken)
            .GetAsyncEnumerator(interactionToken);
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
                        var resolved = (Task<WorkflowInteractionResult>)ready;
                        pendingInteractions.Remove(resolved);
                        if (resolved.IsCanceled && interactionToken.IsCancellationRequested)
                        {
                            await run.CancelRunAsync().ConfigureAwait(false);
                            yield break;
                        }
                        var interaction = await resolved.ConfigureAwait(false);
                        if (interaction.Waiting is { } waitingRequest)
                        {
                            waiting.TryAdd(waitingRequest.InteractionId, waitingRequest);
                        }
                        else if (interaction.Rejection is { } rejection)
                        {
                            await run.CancelRunAsync().ConfigureAwait(false);
                            yield return rejection;
                            state.Outcome = TurnOutcome.Completed;
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
                catch (OperationCanceledException) when (interactionToken.IsCancellationRequested)
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

                        var request =
                            CreateInteractionRequest(externalRequest, humanGateNodes, scope.InteractionRequests)
                            ?? throw new AgwException(
                                ErrorCodes.AgentExecutionFailed,
                                $"External request '{externalRequest.RequestId}' is unsupported."
                            );
                        if (answers.TryGetValue(request.InteractionId, out var answered))
                        {
                            consumed.Add(request.InteractionId);
                            var answer = InteractionRules.ValidateAndNormalize(
                                request,
                                answered.Response,
                                scope.Permissions.Current
                            );
                            if (
                                await SendResponseAsync(run, externalRequest, request, answer).ConfigureAwait(false) is
                                { } answeredRejection
                            )
                            {
                                await run.CancelRunAsync().ConfigureAwait(false);
                                yield return answeredRejection;
                                state.Outcome = TurnOutcome.Completed;
                                yield break;
                            }
                            break;
                        }

                        if (handler is null)
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
                            state.Outcome = TurnOutcome.Completed;
                            yield break;
                        }
                        // 每个独立请求等待回答期间继续消费 Workflow 事件。
                        // Keep consuming workflow events while each independent request awaits its response.
                        pendingInteractions.Add(
                            ResolveWorkflowInteractionAsync(
                                run,
                                externalRequest,
                                request,
                                handler,
                                resources.TraceContext,
                                agentflowId,
                                interactionToken
                            )
                        );
                        break;
                    }

                    case AgentResponseUpdateEvent updateEvt when updateEvt.Data is AgentResponseUpdate:
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
                    {
                        state.CompletedSupersteps++;
                        if (scope.Turn is { } turn)
                            await turn.CompleteStepAsync(state.CompletedSupersteps, cancellationToken)
                                .ConfigureAwait(false);
                        var checkpointMarkers = CreateCheckpointMarkers(pendingCheckpointRequests.Values);
                        var recorded = await _checkpointSupport
                            .RecordCheckpointAsync(
                                scope.Context.TurnId,
                                sessionScope.ProjectId,
                                sessionScope.ConversationId,
                                sessionScope.ContextId,
                                resources.TraceContext.TaskId,
                                agentflowId,
                                scope.UserId,
                                isDurable: scope.Context.Provider == ExecutionProvider.Distributed,
                                definitionFingerprint,
                                checkpointedRun.Store,
                                completed.CompletionInfo?.Checkpoint,
                                checkpointMarkers,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                        if (recorded != null)
                        {
                            runtime.CheckpointState.Register(recorded.Snapshot);
                            foreach (var checkpointMessage in recorded.Messages)
                            {
                                yield return checkpointMessage;
                            }
                        }
                        _checkpointSupport.LogCheckpoint(completed, checkpointMarkers);
                        if (waiting.Count > 0 && completed.CompletionInfo is { HasPendingRequests: true } completion)
                        {
                            if (completion.Checkpoint == null)
                            {
                                throw new AgwException(
                                    ErrorCodes.AgentExecutionFailed,
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

                            ThrowIfAnswerNotRestored(answers, consumed);
                            await run.CancelRunAsync().ConfigureAwait(false);
                            state.Outcome = new TurnOutcome(TurnOutcomeStatus.WaitingForHuman)
                            {
                                PendingInteractions = waiting.Values.ToArray(),
                                Checkpoint =
                                    checkpointedRun.Store.Latest
                                    ?? throw new AgwException(
                                        ErrorCodes.AgentExecutionFailed,
                                        "Agentflow checkpoint was not persisted."
                                    ),
                            };
                            yield break;
                        }

                        await ContinueCheckpointRequestsAsync(run, pendingCheckpointRequests.Values, cancellationToken)
                            .ConfigureAwait(false);
                        pendingCheckpointRequests.Clear();
                        break;
                    }

                    case WorkflowErrorEvent error:
                        _logger.LogError(error.Exception, "Workflow error");
                        yield return CreateWorkflowErrorMessage(error.Exception, Guid.CreateVersion7().Normalize());
                        state.Outcome = TurnOutcome.Failed(error.Exception?.Message ?? "Agentflow execution failed.");
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

        if (cancellationToken.IsCancellationRequested)
        {
            yield break;
        }
        ThrowIfAnswerNotRestored(answers, consumed);
        state.Outcome = TurnOutcome.Completed;
    }

    private static void ThrowIfAnswerNotRestored(
        IReadOnlyDictionary<string, DurableResolvedInteraction> answers,
        IReadOnlySet<string> consumed
    )
    {
        var missing = answers.Keys.FirstOrDefault(id => !consumed.Contains(id));
        if (missing is not null)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                $"Agentflow did not restore human request '{missing}'."
            );
        }
    }

    private static ToolApprovalRequestContent GetToolApproval(ExternalRequest request)
    {
        request.TryGetDataAs<ToolApprovalRequestContent>(out var approval);
        return approval!;
    }

    /// <summary>
    /// 向决定来源请求回答；得到回答后交回 Workflow，HumanGate 被拒绝时返回拒绝消息，决定来源返回等待边界时返回该请求。
    /// Asks the decision source for an answer; an answer goes back to the Workflow, a rejected HumanGate returns its message and a wait boundary returns the request.
    /// </summary>
    private static async Task<WorkflowInteractionResult> ResolveWorkflowInteractionAsync(
        StreamingRun run,
        ExternalRequest externalRequest,
        InteractionRequest request,
        IInteractionHandler handler,
        AgentflowExecutionTraceContext trace,
        Guid agentflowId,
        CancellationToken cancellationToken
    )
    {
        var resolving = handler.ResolveAsync(request, cancellationToken);
        var completed = resolving.IsCompletedSuccessfully ? resolving.Result : null;
        // 同步返回的等待边界表示节点尚未执行，不记录 HumanGate 节点执行。
        // A wait boundary returned synchronously means the node has not run, so no HumanGate execution is recorded.
        if (completed is InteractionResolution.Pending)
        {
            return new WorkflowInteractionResult(Rejection: null, Waiting: request);
        }

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
            var resolution = completed ?? await resolving.ConfigureAwait(false);
            if (resolution is not InteractionResolution.Resolved resolved)
            {
                return new WorkflowInteractionResult(Rejection: null, Waiting: request);
            }
            var rejection = await SendResponseAsync(run, externalRequest, request, resolved.Response)
                .ConfigureAwait(false);
            if (rejection != null)
                activity?.Reject();
            else
                activity?.Complete();
            return new WorkflowInteractionResult(rejection, Waiting: null);
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

    /// <summary>
    /// 把回答交回原请求；HumanGate 被拒绝时不交回，返回拒绝消息，由调用方停止 Workflow。
    /// Sends the answer back to its original request; a rejected HumanGate is not sent and returns its message so the caller stops the Workflow.
    /// </summary>
    private static async Task<AgwMessage?> SendResponseAsync(
        StreamingRun run,
        ExternalRequest externalRequest,
        InteractionRequest request,
        InteractionResponse response
    )
    {
        if (response is WorkflowGateDecision decision)
        {
            if (!decision.Approved)
            {
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
            return null;
        }

        await run.SendResponseAsync(
                externalRequest.CreateResponse(
                    MafApprovalAdapter.CreateWorkflowResponse(GetToolApproval(externalRequest), response)
                )
            )
            .ConfigureAwait(false);
        return null;
    }

    private sealed record WorkflowInteractionResult(AgwMessage? Rejection, InteractionRequest? Waiting);

    private sealed class WorkflowRunState
    {
        public TurnOutcome? Outcome { get; set; }

        public int CompletedSupersteps { get; set; }
    }
}
