using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Summaries;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Contracts.History;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Turns;

/// <summary>
/// 用一个 AgentRuntime 跑一个 Turn 或它的一个 Segment：每一圈模型调用加工具审批是一个 Step，结束时通过执行作用域报告结局。
/// Runs one turn, or one of its segments, with an AgentRuntime: each model call plus its tool approvals is one Step, and the outcome is reported through the execution scope.
/// </summary>
public sealed class AgentTurnExecutor
{
    private readonly IConversationHistoryStore? _historyStore;
    private readonly AgentSessionStateStore? _sessionStateStore;
    private readonly IConversationHandoffProvider? _conversationHandoffProvider;
    private readonly ILogger<AgentTurnExecutor> _logger;

    public AgentTurnExecutor(
        IConversationHistoryStore? historyStore,
        AgentSessionStateStore? sessionStateStore,
        IConversationHandoffProvider? conversationHandoffProvider,
        ILogger<AgentTurnExecutor> logger
    )
    {
        _historyStore = historyStore;
        _sessionStateStore = sessionStateStore;
        _conversationHandoffProvider = conversationHandoffProvider;
        _logger = logger;
    }

    /// <summary>
    /// 执行一个 Turn；首次执行消费用户输入，从存档继续时向恢复的会话注入已保存的回答。
    /// Runs one turn; the first run consumes the user input, a continuation injects the saved answers into the restored session.
    /// </summary>
    internal IAsyncEnumerable<AgwMessage> RunAsync(
        ExecutionScope scope,
        AgentRuntime runtime,
        TurnInput input,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(input);
        return TurnHistory.RunAsync(
            scope,
            _historyStore,
            new ConversationHistoryScope
            {
                ProjectId = runtime._projectId,
                ContextId = runtime._contextId,
                Generation = scope.Generation,
                IsExecutionBound = runtime.SessionStateScope?.ProjectConversationId != Guid.Empty,
            },
            RunWithHistoryAsync(scope, runtime, input, cancellationToken),
            cancellationToken
        );
    }

    private async IAsyncEnumerable<AgwMessage> RunWithHistoryAsync(
        ExecutionScope scope,
        AgentRuntime runtime,
        TurnInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        new MafPermissionState(scope.Permissions).Register(runtime.Session);
        var waiting = new List<InteractionRequest>();
        try
        {
            IReadOnlyList<ChatMessage> requestMessages =
                input.Resume == null
                    ? await CreateExecutionInputMessagesAsync(runtime, input.UserInput, cancellationToken)
                        .ConfigureAwait(false)
                    : [CreateApprovalResponseMessage(input.Resume.ResolvedInteractions, scope.Permissions.Current)];
            await foreach (
                var message in TurnHistory
                    .ObserveAsync(
                        scope,
                        RunStepsAsync(scope, runtime, requestMessages, input.UserInput, waiting, cancellationToken),
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            )
            {
                yield return message;
            }
        }
        finally
        {
            if (runtime.SessionStateScope != null && _sessionStateStore != null)
            {
                await _sessionStateStore.SaveAsync(
                    runtime.AgentType,
                    runtime.SessionStateScope,
                    runtime.Agent,
                    runtime.Session,
                    CancellationToken.None
                );
            }
        }

        if (scope.History is { } history)
            await history.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        scope.ReportOutcome(
            waiting.Count == 0
                ? TurnOutcome.Completed with
                {
                    StepCount = scope.StepIndex,
                }
                : new TurnOutcome(TurnOutcomeStatus.WaitingForHuman)
                {
                    PendingInteractions = waiting,
                    StepCount = scope.StepIndex,
                }
        );
    }

    /// <summary>
    /// 逐个 Step 推进：Agent 管线交出的人工审批批次登记到待处理集合，回答齐全后继续同一个 Step；集合不在进程内等待时结束本 Segment。全部完成后写出工具状态快照与 Result。
    /// Advances Step by Step: the human approval batch handed out by the Agent pipeline is registered in the pending set and the same Step continues once fully answered; when the set does not wait in process the segment ends. Completion writes the tool state snapshots and the Result.
    /// </summary>
    private async IAsyncEnumerable<AgwMessage> RunStepsAsync(
        ExecutionScope scope,
        AgentRuntime runtime,
        IReadOnlyList<ChatMessage> requestMessages,
        AgwUserInput userInput,
        List<InteractionRequest> waiting,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var turnPersistence = new ToolTurnPersistence(
            runtime.Agent,
            runtime.Session,
            (messages, token) => PersistToolBlockMessagesAsync(runtime, messages, token)
        );
        try
        {
            IEnumerable<ChatMessage> currentRequestMessages = requestMessages;
            IReadOnlyList<ChatMessage> finalResponseMessages = [];
            IReadOnlyCollection<string> answered = [];
            var declinedBatches = 0;
            var runOptions = ExecutionRunOptions.With(null, scope.Context);
            while (true)
            {
                var approvals = new List<ToolApprovalRequestContent>();
                var responseUpdates = new List<AgentResponseUpdate>();
                await foreach (
                    var update in runtime.Agent.RunStreamingAsync(
                        currentRequestMessages,
                        runtime.Session,
                        runOptions,
                        cancellationToken
                    )
                )
                {
                    turnPersistence.Record(ToolStateSnapshots.ToMessage(update));
                    responseUpdates.Add(update);
                    approvals.AddRange(update.Contents.OfType<ToolApprovalRequestContent>());

                    var aiMessage = update.ToAiMessage();
                    if (aiMessage?.Contents.Count > 0)
                    {
                        yield return aiMessage;
                    }
                }
                finalResponseMessages = responseUpdates.ToAgentResponse().Messages.ToList();

                // 上一批回答对应的调用已经在本 Step 产生结果。
                // The calls answered by the previous batch produced their results in this Step.
                if (answered.Count > 0)
                {
                    await scope.Interactions.MarkConsumedAsync(answered, cancellationToken).ConfigureAwait(false);
                    answered = [];
                }
                if (approvals.Count == 0)
                {
                    break;
                }

                var decision = await DecideBatchAsync(scope, runtime, approvals, cancellationToken)
                    .ConfigureAwait(false);
                if (decision.Waiting.Count > 0)
                {
                    waiting.AddRange(decision.Waiting);
                    await SaveCheckpointAsync(scope, runtime, TurnCheckpointKind.AwaitingInput, cancellationToken)
                        .ConfigureAwait(false);
                    yield break;
                }
                if (decision.Answered.Count == 0 && ++declinedBatches >= PendingInteractionSet.MaxToolApprovalRounds)
                {
                    throw new AgwException(
                        ErrorCodes.AgentExecutionFailed,
                        $"Tool approval exceeded the limit of {PendingInteractionSet.MaxToolApprovalRounds} rounds."
                    );
                }

                answered = decision.Answered;
                currentRequestMessages = [new ChatMessage(ChatRole.User, decision.Responses) { AuthorName = "human" }];
            }

            // 模型不再请求：在 Result 与终态之前保存 TurnCompleted，恢复时直接生成 Result。
            // The model made no further request: save TurnCompleted before the Result and terminal state so recovery only generates the Result.
            await SaveCheckpointAsync(scope, runtime, TurnCheckpointKind.TurnCompleted, cancellationToken)
                .ConfigureAwait(false);
            var stateSnapshots = await turnPersistence.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var stateSnapshot in stateSnapshots)
            {
                if (stateSnapshot.ToAiMessage() is { } stateMessage)
                {
                    yield return stateMessage;
                }
            }

            var result = await CreateResultAsync(runtime, userInput, finalResponseMessages, cancellationToken)
                .ConfigureAwait(false);
            if (result != null)
            {
                yield return result;
            }
        }
        finally
        {
            if (!turnPersistence.CompletionAttempted)
            {
                try
                {
                    await turnPersistence.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Failed to persist Tool state while finalizing an Agent stream.");
                }
            }
        }
    }

    /// <summary>
    /// 在已提交历史的边界保存 Turn 存档；AwaitingInput 带上当前 Step 的待答请求身份，完整原始请求随会话保存在审批批次中。
    /// Saves the turn checkpoint at a committed history boundary; AwaitingInput carries the unanswered request identities of the current Step, and the complete original requests travel with the session in the approval batch.
    /// </summary>
    private static async Task SaveCheckpointAsync(
        ExecutionScope scope,
        AgentRuntime runtime,
        TurnCheckpointKind kind,
        CancellationToken cancellationToken
    )
    {
        if (scope.Checkpoints == null || scope.Context.Node != null)
            return;
        var barrier =
            scope.History == null
                ? null
                : await scope.History.EnterBarrierAsync(cancellationToken).ConfigureAwait(false);
        await using (barrier)
        {
            var snapshot = scope.Interactions.Snapshot();
            var session = await runtime
                .Agent.SerializeSessionAsync(runtime.Session, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await scope
                .Checkpoints.SaveAsync(
                    new AgentTurnCheckpoint(
                        scope.Context.TurnId,
                        scope.StepIndex,
                        kind,
                        snapshot.ApprovalRounds,
                        JsonSerializer.Serialize(session),
                        scope.History?.CommittedSequence ?? -1,
                        kind == TurnCheckpointKind.AwaitingInput
                            ? snapshot
                                .Entries.Where(entry => entry.Status == PendingInteractionStatus.Pending)
                                .Select(entry => entry.Identity)
                                .ToArray()
                            : []
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 决定一个人工审批批次：决定来源可以就地给出回答；需要用户的请求作为一个批次登记，集合在进程内等待齐全或返回等待边界。
    /// Decides one human approval batch: the decision source may answer in place; requests that need a user are registered as one batch, and the set either waits in process until complete or returns a wait boundary.
    /// </summary>
    private static async Task<BatchDecision> DecideBatchAsync(
        ExecutionScope scope,
        AgentRuntime runtime,
        IReadOnlyList<ToolApprovalRequestContent> approvals,
        CancellationToken cancellationToken
    )
    {
        var handler =
            scope.InteractionHandler
            ?? throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "Tool approval requires an active interactive approval channel."
            );
        var requests = approvals
            .Select(approval =>
                MafApprovalAdapter.CreateRequest(
                    approval,
                    InteractionIdentity.StandaloneNodeId,
                    runtime.Agent.Name,
                    scope.InteractionRequests
                )
            )
            .ToArray();
        var answers = new Dictionary<string, InteractionResponse>(StringComparer.Ordinal);
        var pending = new List<InteractionBatchItem>();
        for (var index = 0; index < approvals.Count; index++)
        {
            var request = requests[index];
            var resolution = await handler.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
            if (resolution is InteractionResolution.Resolved resolved)
            {
                answers[request.InteractionId] = resolved.Response;
                continue;
            }
            pending.Add(
                new InteractionBatchItem(
                    new InteractionIdentity
                    {
                        InteractionId = request.InteractionId,
                        TurnId = scope.Context.TurnId,
                        NodeId = request.Source.NodeId ?? InteractionIdentity.StandaloneNodeId,
                        ActivationIndex = scope.Context.Node?.ActivationIndex ?? 0,
                        StepIndex = scope.StepIndex,
                        ProviderRequestId = approvals[index].RequestId,
                        CallId = approvals[index].ToolCall.CallId,
                    },
                    request
                )
            );
        }

        if (pending.Count > 0)
        {
            var batch = new InteractionBatch(
                InteractionIdentity.ForBatch(
                    pending[0].Identity.NodeId,
                    approvals.Select(approval => approval.RequestId)
                ),
                pending
            );
            await scope.Interactions.RegisterBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            if (!await scope.Interactions.WaitForAnswersAsync(batch.BatchId, cancellationToken).ConfigureAwait(false))
            {
                return new BatchDecision([], [], pending.Select(item => item.Request).ToArray());
            }
            foreach (var entry in scope.Interactions.Snapshot().Entries.Where(entry => entry.BatchId == batch.BatchId))
            {
                answers[entry.Identity.InteractionId] = entry.Response!;
            }
        }

        var responses = new List<AIContent>(approvals.Count);
        for (var index = 0; index < approvals.Count; index++)
        {
            responses.Add(MafApprovalAdapter.CreateResponse(approvals[index], answers[requests[index].InteractionId]));
        }
        return new BatchDecision(responses, pending.Select(item => item.Identity.InteractionId).ToArray(), []);
    }

    private async Task<List<ChatMessage>> CreateExecutionInputMessagesAsync(
        AgentRuntime runtime,
        AgwUserInput input,
        CancellationToken cancellationToken
    )
    {
        var sessionScope = runtime.SessionStateScope;
        if (sessionScope == null)
        {
            return [AgwMessageUtil.CreateUserChatMessage(input)];
        }

        var handoff =
            _conversationHandoffProvider == null
                ? ConversationHandoff.Empty
                : await _conversationHandoffProvider
                    .CreateAsync(
                        sessionScope.ProjectConversationId,
                        AgentRuntimeType.Agent,
                        sessionScope.AgentId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
        return AgwMessageUtil.CreateExecutionInputMessages(
            input,
            AgentRuntimeType.Agent,
            sessionScope.AgentId,
            handoff
        );
    }

    /// <summary>
    /// 把已保存的人工回答还原为 MAF 工具审批响应消息。
    /// Restores the saved human answers as a MAF tool approval response message.
    /// </summary>
    private static ChatMessage CreateApprovalResponseMessage(
        IReadOnlyList<DurableResolvedInteraction> resolvedInteractions,
        AgwPermissionMode? permissionMode
    )
    {
        if (resolvedInteractions.Count == 0)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "A resumed Agent segment requires at least one persisted human response."
            );
        }

        var contents = new List<AIContent>(resolvedInteractions.Count);
        foreach (var resolved in resolvedInteractions)
        {
            var request = resolved.Request;
            var source = request.Source;
            if (
                string.IsNullOrWhiteSpace(source.ToolName)
                || string.IsNullOrWhiteSpace(source.CallId)
                || string.IsNullOrWhiteSpace(source.ProviderRequestId)
            )
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "The interaction has no resumable tool identity."
                );
            var argumentsPayload = request switch
            {
                ToolApprovalInteraction tool => tool.Arguments,
                UserInputInteraction userInput => userInput.Arguments ?? userInput.Payload,
                _ => throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "An Agent can only resume tool interactions."
                ),
            };
            var arguments = argumentsPayload.HasValue
                ? JsonUtil.Deserialize<Dictionary<string, object?>>(argumentsPayload.Value.GetRawText())
                : null;
            var approval = new ToolApprovalRequestContent(
                source.ProviderRequestId,
                new FunctionCallContent(source.CallId, source.ToolName, arguments)
            );
            var decision = InteractionRules.ValidateAndNormalize(request, resolved.Response, permissionMode);
            contents.Add(MafApprovalAdapter.CreateResponse(approval, decision));
        }

        return new ChatMessage(ChatRole.User, contents) { AuthorName = "human" };
    }

    /// <summary>
    /// 一个人工审批批次的结果：交回 Agent 的原生响应、本批由用户回答的交互 ID，或需要等待的请求。
    /// The result of one human approval batch: native responses for the Agent, the interaction IDs answered by the user, or the requests still waiting.
    /// </summary>
    private sealed record BatchDecision(
        List<AIContent> Responses,
        IReadOnlyCollection<string> Answered,
        IReadOnlyList<InteractionRequest> Waiting
    );

    private static Task PersistToolBlockMessagesAsync(
        AgentRuntime runtime,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken
    ) =>
        runtime.ConversationHistoryWriter == null || messages.Count == 0
            ? Task.CompletedTask
            : runtime.ConversationHistoryWriter.AppendAsync(
                runtime._projectId,
                runtime._contextId,
                messages,
                cancellationToken
            );

    /// <summary>
    /// 为启用摘要的 System Agent 生成 Result：配置了 ResponseSchema 时生成结构化结果，否则生成摘要。
    /// Creates the Result of a System Agent with summaries enabled: structured when a ResponseSchema is configured, a summary otherwise.
    /// </summary>
    private static async Task<AgwMessage?> CreateResultAsync(
        AgentRuntime runtime,
        AgwUserInput input,
        IReadOnlyList<ChatMessage> assistantMessages,
        CancellationToken cancellationToken
    )
    {
        if (runtime.AgentType != AgentType.System || !runtime.EnableSummary || runtime.SummaryService == null)
        {
            return null;
        }

        var assistantText = AgentTurnResultText.ExtractLastAssistantText(assistantMessages);
        if (assistantText == null)
        {
            return null;
        }

        if (runtime.UseStructuredResult)
        {
            if (runtime.SummaryService is not IAgentStructuredResultService structuredResultService)
            {
                return null;
            }

            var structuredResult = await structuredResultService
                .CreateStructuredResultAsync(assistantText, runtime._projectId, runtime._contextId, cancellationToken)
                .ConfigureAwait(false);
            return structuredResult.ToAiMessage();
        }

        if (!runtime.SummaryModelProviderId.HasValue)
        {
            return null;
        }

        var userText = string.Concat(input.Contents.OfType<AgwTextContent>().Select(content => content.Content)).Trim();
        var sourceMessages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(userText))
        {
            sourceMessages.Add(new ChatMessage(ChatRole.User, userText));
        }

        sourceMessages.Add(new ChatMessage(ChatRole.Assistant, assistantText.Trim()));

        var result = await runtime
            .SummaryService.CreateResultAsync(
                runtime.SummaryModelProviderId.Value,
                sourceMessages,
                runtime._projectId,
                runtime._contextId,
                customInstructions: null,
                cancellationToken
            )
            .ConfigureAwait(false);
        return result.ToAiMessage();
    }
}
