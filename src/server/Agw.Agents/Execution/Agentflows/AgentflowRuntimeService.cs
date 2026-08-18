using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Store;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Durable;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Summaries;
using Agw.Agents.Execution.Turns;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agentflows;

public record AgentflowExecutionResult(
    string TaskId,
    string ContextId,
    IReadOnlyList<AgwMessage> Messages)
{
    public AgentflowExecutionResult(
        string taskId,
        IReadOnlyList<AgwMessage> messages)
        : this(taskId, taskId, messages)
    {
    }
}

public class AgentflowRuntimeService : IAgentflowRuntimeService
{
    private const string DefaultHumanGateMode = "approval";
    private const string DefaultHumanGatePrompt = "Human approval is required to continue.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<AgentflowRuntimeService> _logger;
    private readonly IRepository<Agentflow> _agentflowRepository;
    private readonly IRepository<AgentflowNode> _agentflowNodeRepository;
    private readonly IRepository<AgentflowEdge> _agentflowEdgeRepository;
    private readonly AgentflowDomainService _agentflowDomainService;
    private readonly IAgentRuntimeService _agentRuntimeService;
    private readonly IProviderSessionState _providerSessionState;
    private readonly IAgentTurnSummaryService _summaryService;
    private readonly IConversationHistoryWriter? _conversationHistoryWriter;
    private readonly AgentSessionStateStore? _sessionStateStore;
    private readonly HumanInteractionContextAccessor? _humanInteractionContextAccessor;
    private readonly AgentflowCheckpointStore? _checkpointStore;
    private readonly IRuntimeTurnContextAccessor? _turnContextAccessor;
    private readonly IConversationHandoffProvider? _conversationHandoffProvider;
    private readonly AgentflowWorkflowCompiler _workflowCompiler = new();

    public AgentflowRuntimeService(
        ILogger<AgentflowRuntimeService> logger,
        IRepository<Agentflow> agentflowRepository,
        IRepository<AgentflowNode> agentflowNodeRepository,
        IRepository<AgentflowEdge> agentflowEdgeRepository,
        AgentflowDomainService agentflowDomainService,
        IAgentRuntimeService agentRuntimeService,
        IProviderSessionState providerSessionState,
        IAgentTurnSummaryService summaryService,
        AgentSessionStateStore? sessionStateStore = null,
        IConversationHistoryWriter? conversationHistoryWriter = null,
        HumanInteractionContextAccessor? humanInteractionContextAccessor = null,
        AgentflowCheckpointStore? checkpointStore = null,
        IRuntimeTurnContextAccessor? turnContextAccessor = null,
        IConversationHandoffProvider? conversationHandoffProvider = null)
    {
        _logger = logger;
        _agentflowRepository = agentflowRepository;
        _agentflowNodeRepository = agentflowNodeRepository;
        _agentflowEdgeRepository = agentflowEdgeRepository;
        _agentflowDomainService = agentflowDomainService;
        _agentRuntimeService = agentRuntimeService;
        _providerSessionState = providerSessionState;
        _summaryService = summaryService;
        _sessionStateStore = sessionStateStore;
        _conversationHistoryWriter = conversationHistoryWriter;
        _humanInteractionContextAccessor = humanInteractionContextAccessor;
        _checkpointStore = checkpointStore;
        _turnContextAccessor = turnContextAccessor;
        _conversationHandoffProvider = conversationHandoffProvider;
    }

    public async Task<string?> GetMermaidAsync(Guid agentflowId, CancellationToken cancellationToken = default)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null)
        {
            return null;
        }

        var workflowLease = await CreateAiWorkflow(agentflow, cancellationToken);
        if (workflowLease == null)
        {
            return null;
        }

        await using (workflowLease)
        {
            var mermaidString = WorkflowVisualizer.ToMermaidString(workflowLease.Workflow);
            _logger.LogInformation("Constructed workflow: {Workflow}", mermaidString);
            return mermaidString;
        }
    }

    /// <summary>
    /// 为指定 Agentflow 创建或恢复 context，并以流式消息执行工作流和处理人工审批。
    /// </summary>
    public IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        Guid agentflowId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null,
        Guid? taskId = null,
        IHumanGateApprovalHandler? humanGateApprovalHandler = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        Guid? conversationId = null,
        PermissionMode? permissionMode = null) =>
        ExecuteStreamingCoreAsync(
            agentflowId,
            CreateUserInput(input),
            cancellationToken,
            projectId,
            contextId,
            taskId,
            humanGateApprovalHandler,
            environmentVariables,
            conversationId,
            new PermissionModeState(permissionMode),
            sourceExecutionId: null,
            checkpointState: null,
            resumeCheckpoint: null);

    internal IAsyncEnumerable<AgwMessage> ExecuteStreamingWithPermissionStateAsync(
        Guid agentflowId,
        AgwUserInput input,
        CancellationToken cancellationToken,
        Guid? projectId,
        string? contextId,
        Guid? taskId,
        IHumanGateApprovalHandler? humanGateApprovalHandler,
        IReadOnlyDictionary<string, string>? environmentVariables,
        Guid? conversationId,
        PermissionModeState permissionState,
        Guid? sourceExecutionId,
        AgentflowCheckpointRuntimeState checkpointState,
        AgentflowCheckpointSnapshot? resumeCheckpoint) =>
        ExecuteStreamingCoreAsync(
            agentflowId,
            input,
            cancellationToken,
            projectId,
            contextId,
            taskId,
            humanGateApprovalHandler,
            environmentVariables,
            conversationId,
            permissionState,
            sourceExecutionId,
            checkpointState,
            resumeCheckpoint);

    private async IAsyncEnumerable<AgwMessage> ExecuteStreamingCoreAsync(
        Guid agentflowId,
        AgwUserInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Guid? projectId,
        string? contextId,
        Guid? taskId,
        IHumanGateApprovalHandler? humanGateApprovalHandler,
        IReadOnlyDictionary<string, string>? environmentVariables,
        Guid? conversationId,
        PermissionModeState permissionState,
        Guid? sourceExecutionId,
        AgentflowCheckpointRuntimeState? checkpointState,
        AgentflowCheckpointSnapshot? resumeCheckpoint)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null)
        {
            yield break;
        }

        var resolvedProjectId = ProjectDefaults.GetDefaultProjectIdentifier(projectId);
        var resolvedContextId = ContextIdUtil.ResolveContextId(contextId); ;
        var resolvedTaskId = taskId ?? Guid.CreateVersion7();
        var executionTraceContext = new AgentflowExecutionTraceContext(
            resolvedProjectId,
            resolvedContextId,
            resolvedTaskId);
        var sessionScope = await CreateSessionScopeAsync(
                resolvedProjectId,
                resolvedContextId,
                resolvedTaskId,
                conversationId,
                cancellationToken,
                permissionState)
            .ConfigureAwait(false);
        var workflowLease = await CreateAiWorkflow(
            agentflow,
            cancellationToken,
            sessionScope,
            executionTraceContext,
            environmentVariables);
        if (workflowLease == null)
        {
            yield break;
        }

        await using var workflowResources = workflowLease;
        var workflow = workflowLease.Workflow;
        var checkpointNodeNames = await GetCheckpointNodeNamesAsync(agentflow.Id);

        var humanGateNodes = (await _agentflowNodeRepository.ListAsync(
                x => x.AgentflowId == agentflow.Id && x.Kind == AgentflowNodeKind.HumanGate))
            .ToDictionary(node => node.NodeId, StringComparer.Ordinal);

        var mermaidString = WorkflowVisualizer.ToMermaidString(workflow);
        _logger.LogInformation("Constructed workflow: {Workflow}", mermaidString);

        var messages = resumeCheckpoint == null
            ? await CreateWorkflowInputMessagesAsync(
                    agentflow.Id,
                    sessionScope.ConversationId,
                    input,
                    cancellationToken)
                .ConfigureAwait(false)
            : [];
        var checkpointedRun = await StartCheckpointedRunAsync(
                workflow,
                messages,
                agentflow.Id,
                resolvedTaskId,
                resumeCheckpoint?.Checkpoint,
                cancellationToken)
            .ConfigureAwait(false);
        await using var run = checkpointedRun.Run;
        var definitionFingerprint = checkpointState != null
            && _checkpointStore != null
            && sessionScope.ConversationId != Guid.Empty
                ? await _checkpointStore
                    .GetDefinitionFingerprintAsync(agentflow.Id, cancellationToken)
                    .ConfigureAwait(false)
                : null;
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var executorsWithUpdates = new HashSet<string>(StringComparer.Ordinal);
        var pendingCheckpointRequests = new Dictionary<string, PendingCheckpointRequest>(
            StringComparer.Ordinal);
        var resumedCheckpointNodeIds = resumeCheckpoint?.Markers
            .Select(item => item.NodeId)
            .ToHashSet(StringComparer.Ordinal) ?? [];

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
                            externalRequest.PortInfo.PortId);

                        if (TryCreateCheckpointRequest(
                                externalRequest,
                                checkpointNodeNames,
                                out var checkpointRequest))
                        {
                            if (resumedCheckpointNodeIds.Remove(checkpointRequest.NodeId))
                            {
                                await ContinueCheckpointRequestsAsync(
                                        run,
                                        [checkpointRequest],
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                pendingCheckpointRequests[externalRequest.RequestId] = checkpointRequest;
                            }
                            break;
                        }

                        if (externalRequest.TryGetDataAs(
                                out Microsoft.Extensions.AI.ToolApprovalRequestContent? toolApprovalRequest))
                        {
                            if (humanGateApprovalHandler == null)
                            {
                                _logger.LogWarning(
                                    "Tool approval {RequestId} has no active approval handler.",
                                    externalRequest.RequestId);
                                await run.CancelRunAsync();
                                yield return CreateToolApprovalUnavailableMessage(toolApprovalRequest);
                                yield return TurnMessageFactory.CreateFinished();
                                yield break;
                            }

                            var toolApprovalGate = ToolApprovalSupport.CreateRequest(
                                toolApprovalRequest,
                                externalRequest.PortInfo.PortId);
                            if (humanGateApprovalHandler.RequiresHumanResponse(toolApprovalGate))
                            {
                                yield return ToolApprovalSupport.CreateMessage(toolApprovalGate);
                            }

                            var toolDecision = await humanGateApprovalHandler
                                .WaitForApprovalAsync(toolApprovalGate, cancellationToken);
                            var response = ToolApprovalSupport.CreateWorkflowResponse(
                                toolApprovalRequest,
                                toolDecision);
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
                            agentflow.Id,
                            humanGateNode.NodeId,
                            humanGateNode.Name,
                            approvalRequest.Messages);

                        if (humanGateApprovalHandler == null)
                        {
                            humanGateActivity.Fail("HumanGateApprovalHandlerUnavailable: No approval handler was provided.");
                            _logger.LogWarning(
                                "HumanGate {PortId} requested approval but no approval handler was provided.",
                                externalRequest.PortInfo.PortId);
                            await run.CancelRunAsync();
                            yield return CreateHumanGateUnavailableMessage(humanGateNode);
                            yield return TurnMessageFactory.CreateFinished();
                            yield break;
                        }

                        var approvalTask = humanGateApprovalHandler
                            .WaitForApprovalAsync(approvalRequest, cancellationToken)
                            .AsTask();

                        yield return CreateHumanGateApprovalRequestMessage(approvalRequest);

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
                            yield return CreateHumanGateRejectedMessage(approvalRequest);
                            yield return TurnMessageFactory.CreateFinished();
                            yield break;
                        }

                        var responseMessages = CreateHumanGateResponseMessages(
                            approvalRequest.Messages,
                            decision);
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
                    _logger.LogInformation("AgentResponseUpdateEvent {ExecutorId}, {Data}", updateEvt.ExecutorId,
                        updateEvt.Data);
                    executorsWithUpdates.Add(updateEvt.ExecutorId);
                    var chatMsg = update.ToAiMessage();
                    if (chatMsg != null)
                    {
                        yield return chatMsg;
                    }

                    break;

                case AgentResponseEvent responseEvt when responseEvt.Data is AgentResponse response:
                    _logger.LogInformation("AgentResponseEvent {ExecutorId}, {Data}", responseEvt.ExecutorId,
                        responseEvt.Data);
                    if (executorsWithUpdates.Contains(responseEvt.ExecutorId))
                    {
                        break;
                    }

                    foreach (var responseMsg in response.Messages.Select(message => message.ToAiMessage()).OfType<AgwMessage>())
                    {
                        yield return responseMsg;
                    }

                    break;

                case WorkflowOutputEvent outputEvt:
                    _logger.LogInformation("Workflow output: {Data}", outputEvt.Data);
                    foreach (var outputMessage in CreateWorkflowOutputMessages(outputEvt.Data))
                    {
                        yield return outputMessage;
                    }

                    break;

                case SuperStepCompletedEvent completed:
                    var checkpointMarkers = CreateCheckpointMarkers(pendingCheckpointRequests.Values);
                    var recorded = await RecordCheckpointAsync(
                            sourceExecutionId,
                            resolvedProjectId,
                            sessionScope.ConversationId,
                            resolvedContextId,
                            resolvedTaskId,
                            agentflow.Id,
                            _turnContextAccessor?.Current?.UserName ?? Constants.AdminUserName,
                            isDurable: false,
                            definitionFingerprint,
                            checkpointedRun.Store,
                            completed.CompletionInfo?.Checkpoint,
                            checkpointMarkers,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (recorded != null)
                    {
                        checkpointState?.Register(recorded.Snapshot);
                        foreach (var checkpointMessage in recorded.Messages)
                        {
                            yield return checkpointMessage;
                        }
                    }
                    LogCheckpoint(completed, checkpointMarkers);
                    await ContinueCheckpointRequestsAsync(
                            run,
                            pendingCheckpointRequests.Values,
                            cancellationToken)
                        .ConfigureAwait(false);
                    pendingCheckpointRequests.Clear();
                    break;

                case WorkflowErrorEvent error:
                    _logger.LogError(error.Exception, "Workflow error");
                    yield return CreateWorkflowErrorMessage(error.Exception);
                    yield return TurnMessageFactory.CreateFinished();
                    yield break;
            }
        }

        yield return TurnMessageFactory.CreateFinished();
    }

    /// <summary>
    /// 执行或恢复一个 Agentflow durable 分段，并把 pending 请求与最新 checkpoint 返回给 PostgreSQL 状态机。
    /// </summary>
    internal async Task<DurableExecutionSegmentResult> ExecuteDurableSegmentAsync(
        DurableExecutionManifest manifest,
        DurableExecutionSegmentInput input,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken)
    {
        if (_humanInteractionContextAccessor == null)
        {
            return CreateDurableFailure(input, "Human interaction context is unavailable.");
        }

        var agentflow = await _agentflowRepository.GetByIdAsync(manifest.AgentId);
        if (agentflow == null)
        {
            return CreateDurableFailure(input, "Agentflow could not be found.");
        }

        var resolvedProjectId = ProjectDefaults.GetDefaultProjectIdentifier(manifest.Task.ProjectId);
        var resolvedContextId = ContextIdUtil.ResolveContextId(manifest.Task.ContextId);
        var executionTraceContext = new AgentflowExecutionTraceContext(
            resolvedProjectId,
            resolvedContextId,
            manifest.Task.TaskId);
        var sessionScope = await CreateSessionScopeAsync(
                resolvedProjectId,
                resolvedContextId,
                manifest.Task.TaskId,
                manifest.Task.ProjectConversationId,
                cancellationToken,
                new PermissionModeState(manifest.Settings.PermissionMode))
            .ConfigureAwait(false);
        var workflowLease = await CreateAiWorkflow(
            agentflow,
            cancellationToken,
            sessionScope,
            executionTraceContext,
            manifest.Settings.EnvironmentVariables,
            deferHumanInteractions: true);
        if (workflowLease == null)
        {
            return CreateDurableFailure(input, "Agentflow could not be constructed.");
        }

        await using var workflowResources = workflowLease;
        using var interactionScope = _humanInteractionContextAccessor.Push(
            new ResolvedHumanInteractionChannel(input.ResolvedInteractions));
        var workflow = workflowLease.Workflow;
        var humanGateNodes = (await _agentflowNodeRepository.ListAsync(
                item => item.AgentflowId == agentflow.Id && item.Kind == AgentflowNodeKind.HumanGate))
            .ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var checkpointNodeNames = await GetCheckpointNodeNamesAsync(agentflow.Id);
        var sessionId = input.Checkpoint?.SessionId ?? $"durable-{manifest.ExecutionId:N}";
        var checkpointStore = new DurableAgentflowCheckpointStore(input.Checkpoint);
        var checkpointManager = CheckpointManager.CreateJson(checkpointStore);
        var definitionFingerprint = _checkpointStore == null
            ? null
            : await _checkpointStore
                .GetDefinitionFingerprintAsync(agentflow.Id, cancellationToken)
                .ConfigureAwait(false);
        StreamingRun run;
        if (input.SegmentIndex == 0)
        {
            var messages = await CreateWorkflowInputMessagesAsync(
                    agentflow.Id,
                    manifest.Task.ProjectConversationId,
                    manifest.Input,
                    cancellationToken)
                .ConfigureAwait(false);
            run = await InProcessExecution.RunStreamingAsync(
                workflow,
                messages,
                checkpointManager,
                sessionId,
                cancellationToken);
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
                cancellationToken);
        }

        await using (run)
        {
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            var responses = input.ResolvedInteractions.ToDictionary(
                item => item.Request.RequestId,
                StringComparer.Ordinal);
            var consumed = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Dictionary<string, DurableHumanInteractionSnapshot>(StringComparer.Ordinal);
            var executorsWithUpdates = new HashSet<string>(StringComparer.Ordinal);
            var pendingCheckpointRequests = new Dictionary<string, PendingCheckpointRequest>(
                StringComparer.Ordinal);
            var resumedCheckpointNodeIds = input.SegmentIndex == 1
                && manifest.ResumeCheckpointOccurrenceId.HasValue
                    ? manifest.ResumeCheckpointNodeIds.ToHashSet(StringComparer.Ordinal)
                    : [];
            await foreach (var evt in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case RequestInfoEvent requestInfo:
                        {
                            var externalRequest = requestInfo.Request;
                            if (TryCreateCheckpointRequest(
                                    externalRequest,
                                    checkpointNodeNames,
                                    out var checkpointRequest))
                            {
                                if (resumedCheckpointNodeIds.Remove(checkpointRequest.NodeId))
                                {
                                    await ContinueCheckpointRequestsAsync(
                                            run,
                                            [checkpointRequest],
                                            cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    pendingCheckpointRequests[externalRequest.RequestId] = checkpointRequest;
                                }
                                break;
                            }

                            var approvalRequest = CreateDurableApprovalRequest(
                                externalRequest,
                                humanGateNodes);
                            if (approvalRequest == null)
                            {
                                return CreateDurableFailure(
                                    input,
                                    $"External request '{externalRequest.RequestId}' is unsupported.");
                            }

                            if (responses.TryGetValue(approvalRequest.RequestId, out var resolved))
                            {
                                await SendDurableResponseAsync(
                                        run,
                                        externalRequest,
                                        approvalRequest,
                                        resolved.Response,
                                        sink,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                                consumed.Add(approvalRequest.RequestId);
                                break;
                            }

                            pending.TryAdd(
                                approvalRequest.RequestId,
                                DurableHumanInteractionMapper.FromRequest(approvalRequest));
                            break;
                        }

                    case AgentResponseUpdateEvent updateEvent
                        when updateEvent.Data is AgentResponseUpdate update:
                        executorsWithUpdates.Add(updateEvent.ExecutorId);
                        if (update.ToAiMessage() is { } updateMessage)
                        {
                            await sink.WriteAsync(updateMessage, cancellationToken).ConfigureAwait(false);
                        }
                        break;

                    case AgentResponseEvent responseEvent
                        when responseEvent.Data is AgentResponse response:
                        if (!executorsWithUpdates.Contains(responseEvent.ExecutorId))
                        {
                            foreach (var message in response.Messages
                                         .Select(item => item.ToAiMessage())
                                         .OfType<AgwMessage>())
                            {
                                await sink.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        break;

                    case WorkflowOutputEvent outputEvent:
                        foreach (var message in CreateWorkflowOutputMessages(outputEvent.Data))
                        {
                            await sink.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                        }
                        break;

                    case WorkflowErrorEvent error:
                        await sink.WriteAsync(
                                CreateWorkflowErrorMessage(error.Exception),
                                cancellationToken)
                            .ConfigureAwait(false);
                        return CreateDurableFailure(
                            input,
                            error.Exception?.Message ?? "Agentflow execution failed.");

                    case SuperStepCompletedEvent completed
                        when completed.CompletionInfo is { HasPendingRequests: true } completion
                             && pending.Count > 0:
                        var waitingCheckpointMarkers = CreateCheckpointMarkers(
                            pendingCheckpointRequests.Values);
                        var waitingCheckpoint = await RecordCheckpointAsync(
                                input.ExecutionId,
                                resolvedProjectId,
                                sessionScope.ConversationId,
                                resolvedContextId,
                                manifest.Task.TaskId,
                                agentflow.Id,
                                manifest.UserName,
                                isDurable: true,
                                definitionFingerprint,
                                checkpointStore,
                                completion.Checkpoint,
                                waitingCheckpointMarkers,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (waitingCheckpoint != null)
                        {
                            foreach (var checkpointMessage in waitingCheckpoint.Messages)
                            {
                                await sink.WriteAsync(checkpointMessage, cancellationToken)
                                .ConfigureAwait(false);
                            }
                        }
                        LogCheckpoint(completed, waitingCheckpointMarkers);
                        if (completion.Checkpoint == null)
                        {
                            return CreateDurableFailure(
                                input,
                                "Agentflow reached a human interaction without a checkpoint.");
                        }

                        if (pendingCheckpointRequests.Count > 0)
                        {
                            await ContinueCheckpointRequestsAsync(
                                    run,
                                    pendingCheckpointRequests.Values,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            pendingCheckpointRequests.Clear();
                            break;
                        }

                        await run.CancelRunAsync().ConfigureAwait(false);
                        var durableCheckpoint = checkpointStore.Latest;
                        if (durableCheckpoint == null)
                        {
                            return CreateDurableFailure(
                                input,
                                "Agentflow checkpoint was not persisted.");
                        }

                        return new DurableExecutionSegmentResult
                        {
                            ExecutionId = input.ExecutionId,
                            SegmentIndex = input.SegmentIndex,
                            Status = DurableExecutionSegmentStatus.WaitingForHuman,
                            PendingInteractions = pending.Values.ToArray(),
                            Checkpoint = durableCheckpoint
                        };

                    case SuperStepCompletedEvent completed:
                        var checkpointMarkers = CreateCheckpointMarkers(
                            pendingCheckpointRequests.Values);
                        var recordedCheckpoint = await RecordCheckpointAsync(
                                input.ExecutionId,
                                resolvedProjectId,
                                sessionScope.ConversationId,
                                resolvedContextId,
                                manifest.Task.TaskId,
                                agentflow.Id,
                                manifest.UserName,
                                isDurable: true,
                                definitionFingerprint,
                                checkpointStore,
                                completed.CompletionInfo?.Checkpoint,
                                checkpointMarkers,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (recordedCheckpoint != null)
                        {
                            foreach (var checkpointMessage in recordedCheckpoint.Messages)
                            {
                                await sink.WriteAsync(checkpointMessage, cancellationToken)
                                .ConfigureAwait(false);
                            }
                        }
                        LogCheckpoint(completed, checkpointMarkers);
                        await ContinueCheckpointRequestsAsync(
                                run,
                                pendingCheckpointRequests.Values,
                                cancellationToken)
                            .ConfigureAwait(false);
                        pendingCheckpointRequests.Clear();
                        break;
                }
            }

            var missingResponses = responses.Keys.Except(consumed, StringComparer.Ordinal).ToArray();
            if (missingResponses.Length > 0)
            {
                return CreateDurableFailure(
                    input,
                    $"Agentflow did not restore human request '{missingResponses[0]}'.");
            }

            return new DurableExecutionSegmentResult
            {
                ExecutionId = input.ExecutionId,
                SegmentIndex = input.SegmentIndex,
                Status = DurableExecutionSegmentStatus.Completed
            };
        }
    }

    public async Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        Guid taskId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, input)
            {
                AuthorName = Constants.DefaultInputAuthor
            }
        };

        return await ExecuteAsync(agentflowId, taskId, messages, cancellationToken, projectId, contextId)
            .ConfigureAwait(false);
    }

    public async Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        Guid taskId,
        List<ChatMessage> messages,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null)
    {
        return await ExecuteAsync(
            agentflowId,
            ProjectDefaults.GetDefaultProjectIdentifier(projectId),
            taskId,
            messages,
            cancellationToken,
            contextId).ConfigureAwait(false);
    }

    public async Task<AgentflowWorkflowLease?> CreateAiWorkflow(
        Guid agentflowId,
        CancellationToken cancellationToken = default)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null)
        {
            return null;
        }

        return await CreateAiWorkflow(agentflow, cancellationToken);
    }

    private async Task<AgentflowAgentSessionScope> CreateSessionScopeAsync(
        Guid projectId,
        string contextId,
        Guid? taskId,
        Guid? conversationId,
        CancellationToken cancellationToken,
        PermissionModeState permissionState)
    {
        var resolvedConversationId =
            conversationId.HasValue && conversationId.Value != Guid.Empty
                ? conversationId.Value
                : await ResolveProjectConversationIdAsync(projectId, contextId, cancellationToken)
                    .ConfigureAwait(false);
        return new AgentflowAgentSessionScope(
            _providerSessionState,
            projectId,
            contextId.Trim(),
            taskId,
            _sessionStateStore,
            _conversationHistoryWriter,
            resolvedConversationId,
            permissionState);
    }

    private async Task<Guid> ResolveProjectConversationIdAsync(
        Guid projectId,
        string contextId,
        CancellationToken cancellationToken)
    {
        if (_sessionStateStore == null)
        {
            return Guid.Empty;
        }

        return await _sessionStateStore
            .ResolveProjectConversationIdAsync(projectId, contextId, cancellationToken)
            .ConfigureAwait(false) ?? Guid.Empty;
    }

    private async Task<AgentflowWorkflowLease?> CreateAiWorkflow(
        Guid agentflowId,
        CancellationToken cancellationToken,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        bool deferHumanInteractions = false)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null)
        {
            return null;
        }

        return await CreateAiWorkflow(
            agentflow,
            cancellationToken,
            sessionScope,
            executionTraceContext,
            environmentVariables,
            deferHumanInteractions);
    }

    /// <summary>
    /// 使用已转换的聊天消息执行 Agentflow，并返回归一化 context 下的完整执行结果。
    /// </summary>
    private async Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        Guid projectId,
        Guid? taskId,
        List<ChatMessage> messages,
        CancellationToken cancellationToken,
        string? contextId = null)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null)
        {
            return null;
        }

        if (taskId == null)
        {
            taskId = Guid.CreateVersion7();
        }

        var resolvedContextId = ContextIdUtil.ResolveContextId(contextId);
        var executionTraceContext = new AgentflowExecutionTraceContext(
            projectId,
            resolvedContextId,
            taskId.Value);
        var sessionScope = await CreateSessionScopeAsync(
                projectId,
                resolvedContextId,
                taskId,
                conversationId: null,
                cancellationToken,
                new PermissionModeState(permissionMode: null))
            .ConfigureAwait(false);
        var workflowLease = await CreateAiWorkflow(
            agentflow,
            cancellationToken,
            sessionScope,
            executionTraceContext);
        if (workflowLease == null)
        {
            return null;
        }

        await using var workflowResources = workflowLease;
        var workflow = workflowLease.Workflow;
        var checkpointNodeNames = await GetCheckpointNodeNamesAsync(agentflow.Id);

        var checkpointedRun = await StartCheckpointedRunAsync(
                workflow,
                messages,
                agentflow.Id,
                taskId.Value,
                resumeCheckpoint: null,
                cancellationToken)
            .ConfigureAwait(false);
        await using var run = checkpointedRun.Run;
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var outputs = new List<AgwMessage>();
        var pendingCheckpointRequests = new Dictionary<string, PendingCheckpointRequest>(
            StringComparer.Ordinal);
        await foreach (var evt in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            if (evt is RequestInfoEvent requestInfo)
            {
                if (TryCreateCheckpointRequest(
                        requestInfo.Request,
                        checkpointNodeNames,
                        out var checkpointRequest))
                {
                    pendingCheckpointRequests[requestInfo.Request.RequestId] = checkpointRequest;
                    continue;
                }

                await run.CancelRunAsync();
                if (!requestInfo.Request.TryGetDataAs(
                        out Microsoft.Extensions.AI.ToolApprovalRequestContent? toolApprovalRequest))
                {
                    throw new AgwException(
                        ErrorCodes.AgentExecutionFailed,
                        $"External request '{requestInfo.Request.RequestId}' cannot be handled during unattended Agentflow execution.");
                }

                throw new AgwException(
                    ErrorCodes.AgentExecutionFailed,
                    $"Tool approval '{toolApprovalRequest.RequestId}' cannot be requested during unattended Agentflow execution.");
            }
            else if (evt is AgentResponseUpdateEvent updateEvt)
            {
                _logger.LogDebug("{ExecutorId}: {Data}", updateEvt.ExecutorId, updateEvt.Data);
            }
            else if (evt is WorkflowOutputEvent outputEvt)
            {
                outputs.AddRange(CreateWorkflowOutputMessages(outputEvt.Data));
            }
            else if (evt is SuperStepCompletedEvent completed)
            {
                var checkpointMarkers = CreateCheckpointMarkers(
                    pendingCheckpointRequests.Values);
                LogCheckpoint(completed, checkpointMarkers);
                await ContinueCheckpointRequestsAsync(
                        run,
                        pendingCheckpointRequests.Values,
                        cancellationToken)
                    .ConfigureAwait(false);
                pendingCheckpointRequests.Clear();
            }
        }

        var taskIdString = taskId.Value.Normalize();

        return new AgentflowExecutionResult(taskIdString, resolvedContextId, outputs);
    }

    internal static IReadOnlyList<AgwMessage> CreateWorkflowOutputMessages(object? data)
    {
        return data switch
        {
            null => [],
            ChatMessage message => ConvertChatMessages([message]),
            IEnumerable<ChatMessage> messages => ConvertChatMessages(messages),
            AgentResponse response => ConvertChatMessages(response.Messages),
            IEnumerable<AgentResponse> responses => responses
                .SelectMany(response => ConvertChatMessages(response.Messages))
                .ToList(),
            AgentResponseUpdate update => update.ToAiMessage() is { } message ? [message] : [],
            IEnumerable<AgentResponseUpdate> updates => updates
                .Select(update => update.ToAiMessage())
                .OfType<AgwMessage>()
                .ToList(),
            _ => [],
        };
    }

    internal static List<ChatMessage> CreateWorkflowInputMessages(string input) =>
        [AgwMessageUtil.CreateUserChatMessage(CreateUserInput(input))];

    private async Task<List<ChatMessage>> CreateWorkflowInputMessagesAsync(
        Guid agentflowId,
        Guid conversationId,
        AgwUserInput input,
        CancellationToken cancellationToken)
    {
        var handoff = _conversationHandoffProvider == null
            ? ConversationHandoff.Empty
            : await _conversationHandoffProvider.CreateAsync(
                    conversationId,
                    AgentRuntimeType.Agentflow,
                    agentflowId,
                    cancellationToken)
                .ConfigureAwait(false);
        return CreateWorkflowInputMessages(input, agentflowId, handoff);
    }

    internal static List<ChatMessage> CreateWorkflowInputMessages(
        AgwUserInput input,
        Guid agentflowId,
        ConversationHandoff handoff) =>
        AgwMessageUtil.CreateExecutionInputMessages(
            input,
            AgentRuntimeType.Agentflow,
            agentflowId,
            handoff);

    private static AgwUserInput CreateUserInput(string input) => new()
    {
        Author = Constants.DefaultInputAuthor,
        Contents = [new AgwTextContent { Content = input }]
    };

    private static IReadOnlyList<AgwMessage> ConvertChatMessages(IEnumerable<ChatMessage> messages)
    {
        return messages
            .Select(message => message.ToAiMessage())
            .OfType<AgwMessage>()
            .ToList();
    }

    private async Task<AgentflowWorkflowLease?> CreateAiWorkflow(
        Agentflow agentflow,
        CancellationToken cancellationToken,
        AgentflowAgentSessionScope? sessionScope = null,
        AgentflowExecutionTraceContext? executionTraceContext = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        bool deferHumanInteractions = false)
    {
        var agentflowNodes = await _agentflowNodeRepository.ListAsync(x => x.AgentflowId == agentflow.Id);
        var agentflowEdges = await _agentflowEdgeRepository.ListAsync(x => x.AgentflowId == agentflow.Id);
        if (agentflowNodes.Count == 0)
        {
            return null;
        }

        var orderedNodes = _agentflowDomainService.OrderNodesByEdges(agentflowNodes, agentflowEdges);
        var nodeIdToAgent = new Dictionary<string, AIAgent>(StringComparer.Ordinal);
        var resources = new AgentResourceLease();

        try
        {
            foreach (var node in orderedNodes)
            {
                AIAgent? aiAgent;
                if (node.Kind == AgentflowNodeKind.Agent && node.RelateId.HasValue)
                {
                    aiAgent = await _agentRuntimeService.CreateAgentflowNodeAgentAsync(
                        node.RelateId.Value,
                        sessionScope?.ProjectId,
                        sessionScope?.ConversationId ?? Guid.Empty,
                        environmentVariables,
                        deferHumanInteractions,
                        cancellationToken: cancellationToken);
                    if (aiAgent != null)
                    {
                        resources.Add(new AgentflowAgentLifetime(aiAgent));
                    }
                }
                else if (node.Kind == AgentflowNodeKind.WorkflowAsAgent && node.RelateId.HasValue)
                {
                    var flowNode = await CreateAiWorkflow(
                        node.RelateId.Value,
                        cancellationToken,
                        sessionScope,
                        executionTraceContext,
                        environmentVariables,
                        deferHumanInteractions);
                    if (flowNode == null)
                    {
                        await DisposeWorkflowResourcesWithoutThrowingAsync(resources).ConfigureAwait(false);
                        return null;
                    }

                    resources.Add(flowNode);
                    aiAgent = flowNode.Workflow.AsAIAgent();
                }
                else
                {
                    continue;
                }

                if (aiAgent == null)
                {
                    await DisposeWorkflowResourcesWithoutThrowingAsync(resources).ConfigureAwait(false);
                    return null;
                }

                nodeIdToAgent[node.NodeId] = aiAgent;
            }

            if (nodeIdToAgent.Count == 0)
            {
                await DisposeWorkflowResourcesWithoutThrowingAsync(resources).ConfigureAwait(false);
                return null;
            }

            var summaryContext = sessionScope != null && agentflow.SummaryModelProviderId.HasValue
                ? new AgentflowSummaryContext(
                    _summaryService,
                    agentflow.SummaryModelProviderId.Value,
                    sessionScope.ProjectId,
                    sessionScope.ContextId)
                : null;
            var workflow = _workflowCompiler.Compile(
                agentflow,
                orderedNodes,
                agentflowEdges,
                nodeIdToAgent,
                sessionScope,
                executionTraceContext,
                summaryContext);
            if (workflow == null)
            {
                await DisposeWorkflowResourcesWithoutThrowingAsync(resources).ConfigureAwait(false);
                return null;
            }

            return new AgentflowWorkflowLease(workflow, resources);
        }
        catch
        {
            await DisposeWorkflowResourcesWithoutThrowingAsync(resources).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask DisposeWorkflowResourcesWithoutThrowingAsync(IAsyncDisposable resources)
    {
        try
        {
            await resources.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string CreateCheckpointSessionId(Guid agentflowId, Guid taskId) =>
        $"agentflow-{agentflowId:N}-task-{taskId:N}";

    private static async ValueTask<CheckpointedStreamingRun> StartCheckpointedRunAsync(
        Workflow workflow,
        List<ChatMessage> messages,
        Guid agentflowId,
        Guid taskId,
        DurableAgentflowCheckpoint? resumeCheckpoint,
        CancellationToken cancellationToken)
    {
        var store = new DurableAgentflowCheckpointStore(resumeCheckpoint);
        var checkpointManager = CheckpointManager.CreateJson(store);
        if (resumeCheckpoint == null)
        {
            var run = await InProcessExecution.RunStreamingAsync(
                    workflow,
                    messages,
                    checkpointManager,
                    CreateCheckpointSessionId(agentflowId, taskId),
                    cancellationToken)
                .ConfigureAwait(false);
            return new CheckpointedStreamingRun(run, store);
        }

        var checkpoint = await checkpointManager
            .GetLatestCheckpointAsync(resumeCheckpoint.SessionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "Agentflow checkpoint could not be restored.");
        var resumedRun = await InProcessExecution.ResumeStreamingAsync(
                workflow,
                checkpoint,
                checkpointManager,
                cancellationToken)
            .ConfigureAwait(false);
        return new CheckpointedStreamingRun(resumedRun, store);
    }

    private async Task<RecordedAgentflowCheckpoint?> RecordCheckpointAsync(
        Guid? sourceExecutionId,
        Guid projectId,
        Guid conversationId,
        string contextId,
        Guid taskId,
        Guid agentflowId,
        string userName,
        bool isDurable,
        string? definitionFingerprint,
        DurableAgentflowCheckpointStore runCheckpointStore,
        CheckpointInfo? frameworkCheckpoint,
        IReadOnlyDictionary<string, string> checkpointMarkers,
        CancellationToken cancellationToken)
    {
        if (_checkpointStore == null
            || conversationId == Guid.Empty
            || checkpointMarkers.Count == 0
            || frameworkCheckpoint == null
            || string.IsNullOrWhiteSpace(definitionFingerprint))
        {
            return null;
        }

        var checkpoint = runCheckpointStore.Latest;
        if (checkpoint == null
            || !string.Equals(
                checkpoint.SessionId,
                frameworkCheckpoint.SessionId,
                StringComparison.Ordinal)
            || !string.Equals(
                checkpoint.CheckpointId,
                frameworkCheckpoint.CheckpointId,
                StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Agentflow checkpoint markers were emitted without a persisted framework checkpoint.");
            return null;
        }

        return await _checkpointStore.RecordAsync(
                sourceExecutionId,
                projectId,
                conversationId,
                contextId,
                taskId,
                agentflowId,
                userName,
                isDurable,
                definitionFingerprint,
                checkpoint,
                checkpointMarkers,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record CheckpointedStreamingRun(
        StreamingRun Run,
        DurableAgentflowCheckpointStore Store);

    private async Task<IReadOnlyDictionary<string, CheckpointRequestNode>> GetCheckpointNodeNamesAsync(
        Guid agentflowId)
    {
        var nodes = await _agentflowNodeRepository.ListAsync(
            item => item.AgentflowId == agentflowId
                && item.Kind == AgentflowNodeKind.CheckpointMarker);
        return nodes.ToDictionary(
            item => AgentflowWorkflowCompiler.GetCheckpointRequestPortId(item.NodeId),
            item => new CheckpointRequestNode(
                item.NodeId,
                AgentflowWorkflowCompiler.ResolveCheckpointName(item)),
            StringComparer.Ordinal);
    }

    private static bool TryCreateCheckpointRequest(
        ExternalRequest request,
        IReadOnlyDictionary<string, CheckpointRequestNode> checkpointNodes,
        out PendingCheckpointRequest checkpointRequest)
    {
        if (!checkpointNodes.TryGetValue(request.PortInfo.PortId, out var checkpointNode))
        {
            checkpointRequest = null!;
            return false;
        }

        var messages = request.TryGetDataAs<List<ChatMessage>>(out var requestedMessages)
            && requestedMessages != null
                ? requestedMessages
                : [];
        checkpointRequest = new PendingCheckpointRequest(
            request,
            checkpointNode.NodeId,
            checkpointNode.Name,
            messages);
        return true;
    }

    private static IReadOnlyDictionary<string, string> CreateCheckpointMarkers(
        IEnumerable<PendingCheckpointRequest> requests) =>
        requests
            .GroupBy(item => item.NodeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().CheckpointName,
                StringComparer.Ordinal);

    private static async Task ContinueCheckpointRequestsAsync(
        StreamingRun run,
        IEnumerable<PendingCheckpointRequest> requests,
        CancellationToken cancellationToken)
    {
        foreach (var request in requests)
        {
            await run.SendResponseAsync(
                    request.Request.CreateResponse(request.Messages))
                .ConfigureAwait(false);
        }
    }

    private sealed record PendingCheckpointRequest(
        ExternalRequest Request,
        string NodeId,
        string CheckpointName,
        List<ChatMessage> Messages);

    private sealed record CheckpointRequestNode(string NodeId, string Name);

    private void LogCheckpoint(
        SuperStepCompletedEvent completed,
        IReadOnlyDictionary<string, string> checkpointMarkers)
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
                completed.StepNumber);
        }
    }

    /// <summary>
    /// 将 Agentflow external request 映射为可持久化的 Tool approval 或 HumanGate 请求。
    /// </summary>
    private static HumanGateApprovalRequest? CreateDurableApprovalRequest(
        ExternalRequest externalRequest,
        IReadOnlyDictionary<string, AgentflowNode> humanGateNodes)
    {
        if (externalRequest.TryGetDataAs(
                out Microsoft.Extensions.AI.ToolApprovalRequestContent? toolApprovalRequest))
        {
            return ToolApprovalSupport.CreateRequest(
                toolApprovalRequest,
                externalRequest.PortInfo.PortId);
        }

        return humanGateNodes.TryGetValue(externalRequest.PortInfo.PortId, out var humanGateNode)
            ? CreateHumanGateApprovalRequest(externalRequest, humanGateNode)
            : null;
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
        CancellationToken cancellationToken)
    {
        if (request.ToolApprovalRequest is { } toolApprovalRequest)
        {
            var decision = new HumanGateApprovalDecision(
                response.RequestId,
                response.Approved,
                response.ResponseText,
                response.ApprovalScope,
                response.ResponseData);
            await run.SendResponseAsync(
                    externalRequest.CreateResponse(
                        ToolApprovalSupport.CreateWorkflowResponse(toolApprovalRequest, decision)))
                .ConfigureAwait(false);
            return;
        }

        var humanDecision = new HumanGateApprovalDecision(
            response.RequestId,
            response.Approved,
            response.ResponseText,
            response.ApprovalScope,
            response.ResponseData);
        if (!response.Approved)
        {
            await sink.WriteAsync(CreateHumanGateRejectedMessage(request), cancellationToken)
                .ConfigureAwait(false);
            await run.CancelRunAsync().ConfigureAwait(false);
            return;
        }

        await run.SendResponseAsync(
                externalRequest.CreateResponse(
                    CreateHumanGateResponseMessages(request.Messages, humanDecision)))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 创建与当前 execution 和 segment 对齐的 Agentflow 失败结果。
    /// </summary>
    private static DurableExecutionSegmentResult CreateDurableFailure(
        DurableExecutionSegmentInput input,
        string error) =>
        new()
        {
            ExecutionId = input.ExecutionId,
            SegmentIndex = input.SegmentIndex,
            Status = DurableExecutionSegmentStatus.Failed,
            ErrorMessage = error
        };

    private static HumanGateApprovalRequest CreateHumanGateApprovalRequest(
        ExternalRequest externalRequest,
        AgentflowNode node)
    {
        var config = ReadHumanGateConfig(node);
        var messages = externalRequest.TryGetDataAs<List<ChatMessage>>(out var requestedMessages) &&
            requestedMessages != null
                ? requestedMessages
                : [];

        var mode = string.IsNullOrWhiteSpace(config.HumanMode)
            ? DefaultHumanGateMode
            : config.HumanMode.Trim();
        var prompt = string.IsNullOrWhiteSpace(config.HumanPrompt)
            ? DefaultHumanGatePrompt
            : config.HumanPrompt.Trim();

        return new HumanGateApprovalRequest(
            externalRequest.RequestId,
            node.NodeId,
            node.Name,
            mode,
            prompt,
            messages);
    }

    private static List<ChatMessage> CreateHumanGateResponseMessages(
        IReadOnlyList<ChatMessage> messages,
        HumanGateApprovalDecision decision)
    {
        var responseMessages = messages.ToList();
        responseMessages.Add(new ChatMessage(
            ChatRole.User,
            string.IsNullOrWhiteSpace(decision.ResponseText) ? string.Empty : decision.ResponseText.Trim())
        {
            AuthorName = "human",
        });

        return responseMessages;
    }

    private static AgwMessage CreateHumanGateApprovalRequestMessage(HumanGateApprovalRequest request)
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { "type", "human-gate-request" },
            { "requestId", request.RequestId },
            { "nodeId", request.NodeId },
            { "mode", request.Mode },
            { "prompt", request.Prompt },
        };

        if (!string.IsNullOrWhiteSpace(request.NodeName))
        {
            additionalProperties["nodeName"] = request.NodeName;
        }

        var latestMessageText = request.Messages.LastOrDefault()?.Text;
        if (!string.IsNullOrWhiteSpace(latestMessageText))
        {
            additionalProperties["inputPreview"] = latestMessageText;
        }

        return new AgwMessage(
            Guid.CreateVersion7().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = request.Prompt }],
            additionalProperties);
    }

    private static AgwMessage CreateHumanGateRejectedMessage(HumanGateApprovalRequest request)
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { "type", "human-gate-rejected" },
            { "requestId", request.RequestId },
            { "nodeId", request.NodeId },
        };

        return new AgwMessage(
            Guid.CreateVersion7().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = "HumanGate rejected. Workflow stopped." }],
            additionalProperties);
    }

    private static AgwMessage CreateHumanGateUnavailableMessage(AgentflowNode node)
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { "type", "human-gate-unavailable" },
            { "nodeId", node.NodeId },
        };

        return new AgwMessage(
            Guid.CreateVersion7().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = "HumanGate requires an active approval channel." }],
            additionalProperties);
    }

    private static AgwMessage CreateToolApprovalUnavailableMessage(
        Microsoft.Extensions.AI.ToolApprovalRequestContent request)
    {
        var properties = new AdditionalPropertiesDictionary
        {
            { "type", "tool-approval-unavailable" },
            { "requestId", request.RequestId }
        };
        return new AgwMessage(
            Guid.CreateVersion7().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent
            {
                Content = "Tool approval requires an active interactive approval channel."
            }],
            properties);
    }

    private static AgwMessage CreateWorkflowErrorMessage(Exception? exception)
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { "type", "workflow-error" },
        };

        return new AgwMessage(
            Guid.CreateVersion7().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = exception?.Message ?? "Workflow execution failed." }],
            additionalProperties);
    }

    private static HumanGateConfig ReadHumanGateConfig(AgentflowNode node)
    {
        if (string.IsNullOrWhiteSpace(node.ConfigJson))
        {
            return new HumanGateConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<HumanGateConfig>(node.ConfigJson, JsonOptions) ??
                new HumanGateConfig();
        }
        catch (JsonException)
        {
            return new HumanGateConfig();
        }
    }

    private sealed record HumanGateConfig
    {
        public string? HumanMode { get; init; }

        public string? HumanPrompt { get; init; }
    }

}
