using System.Runtime.ExceptionServices;
using Agw.Agents.Execution.Agents.Contracts;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Summaries;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using RuntimeAgentExecutionResult = Agw.Agents.Execution.Agents.Contracts.AgentExecutionResult;

namespace Agw.Agents.Execution.Agents.Runtime;

public partial class AgentRuntimeService
{
    public IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentRuntime session,
        AgwUserInput input,
        CancellationToken cancellationToken = default
    ) => _turnExecutor.ExecuteStreamingAsync(session, input, cancellationToken);

    public IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentRuntime session,
        AgwUserInput input,
        IInteractionHandler? approvalHandler,
        CancellationToken cancellationToken = default
    ) => _turnExecutor.ExecuteStreamingAsync(session, input, approvalHandler, cancellationToken);

    public Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgentRuntime session,
        AgwUserInput input,
        CancellationToken cancellationToken = default
    ) => _turnExecutor.ExecuteAsync(session, input, cancellationToken);

    public Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgentRuntime session,
        AgwUserInput input,
        IInteractionHandler? approvalHandler,
        CancellationToken cancellationToken = default
    ) => _turnExecutor.ExecuteAsync(session, input, approvalHandler, cancellationToken);

    internal IAsyncEnumerable<AgwMessage> ExecuteDurableSegmentStreamingAsync(
        AgentRuntime session,
        ChatMessage message,
        AgwUserInput summaryInput,
        IInteractionHandler approvalHandler,
        CancellationToken cancellationToken = default
    ) =>
        _turnExecutor.ExecuteDurableSegmentStreamingAsync(
            session,
            message,
            summaryInput,
            approvalHandler,
            cancellationToken
        );

    public async Task<RuntimeAgentExecutionResult?> ExecuteByIdAsync(
        AgentExecuteByIdRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var agent = await _agentAppService.GetAgentForCurrentUserAsync(request.AgentId);
        if (agent == null)
        {
            return null;
        }

        var req = new AgentExecuteRequest
        {
            Agent = agent,
            PermissionMode = request.PermissionMode,
            Input = request.Input,
            TaskId = request.TaskId,
            ProjectId = request.ProjectId,
            ContextId = request.ContextId,
        };
        return await ExecuteAsync(req, cancellationToken);
    }

    /// <summary>
    /// 解析执行上下文、恢复 Agent 会话并执行请求，同时持久化会话和可选摘要。
    /// </summary>
    private async Task<RuntimeAgentExecutionResult?> ExecuteAsync(
        AgentExecuteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        Guid? taskId = request.TaskId;
        List<ChatMessage> chatMsg = request.Input;
        Guid? projectId = request.ProjectId;
        string? contextId = request.ContextId;
        Agent agent = request.Agent;

        projectId = await ResolveProjectIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (!projectId.HasValue)
        {
            return null;
        }
        var workspaceSnapshot = ProjectWorkspaceContext.Get(projectId.Value);
        if (workspaceSnapshot == null)
        {
            var project = await _projectRuntimeFacade.GetForCurrentUserAsync(projectId.Value, cancellationToken);
            if (project == null)
                return null;
            workspaceSnapshot = ProjectWorkspacePaths.CreateSnapshot(
                project.Id,
                project.Workspace,
                (project.AdditionalDirectories ?? []).Select(directory => new ProjectWorkspaceDirectory(
                    directory.Id,
                    directory.Path
                ))
            );
        }
        using var workspaceScope = ProjectWorkspaceContext.Push(projectId.Value, workspaceSnapshot);
        var resolvedContextId = ContextIdUtil.ResolveContextId(contextId);
        var conversationId = await _sessionStateStore
            .ResolveProjectConversationIdAsync(projectId.Value, resolvedContextId, cancellationToken)
            .ConfigureAwait(false);
        var aiAgent = await CreateAiAgentAsync(
            new CreateAiAgentRequest
            {
                Agent = agent,
                PermissionMode = request.PermissionMode,
                ProjectId = projectId,
                ConversationId = conversationId ?? Guid.Empty,
                DeferHumanInteractions = true,
            },
            cancellationToken
        );
        if (aiAgent == null)
        {
            throw new AgwException(ErrorCodes.AiAgentCreationFailed);
        }

        await using var historyScope = ConversationHistoryPersistenceContext.BeginScope(
            _chatHistoryProvider as IConversationHistoryPersistence,
            projectId.Value,
            resolvedContextId,
            ConversationSessionContext.GetGeneration(projectId.Value, resolvedContextId),
            allowCreateConversation: !conversationId.HasValue
        );
        AgentSession? session = null;
        AgentSessionStateScope? sessionScope = null;
        ToolTurnPersistence? turnPersistence = null;
        Exception? executionFailure = null;
        try
        {
            taskId ??= Guid.CreateVersion7();
            string taskIdValue = taskId.Value.Normalize();
            sessionScope = new AgentSessionStateScope(
                conversationId ?? Guid.Empty,
                projectId.Value,
                resolvedContextId,
                agent.Id
            );
            session = await _sessionStateStore
                .GetOrCreateAsync(agent, aiAgent, sessionScope, cancellationToken)
                .ConfigureAwait(false);

            _providerSessionState.InitializeSessionState(
                session,
                resolvedContextId,
                ProjectDefaults.GetDefaultProjectIdentifier(projectId)
            );

            MafSessionApprovalState.Apply(session, request.PermissionMode);
            turnPersistence = new ToolTurnPersistence(
                aiAgent,
                session,
                (messages, token) => PersistToolBlockMessagesAsync(projectId.Value, resolvedContextId, messages, token)
            );
            var finalResponseMessages = new List<ChatMessage>();
            var messages = await CollectStreamingMessagesAsync(
                    aiAgent,
                    chatMsg,
                    session,
                    turnPersistence,
                    cancellationToken,
                    new UnattendedInteractionHandler(request.PermissionMode),
                    finalResponseMessages
                )
                .ConfigureAwait(false);
            messages = await AppendDefinitionSummaryAsync(
                    agent,
                    chatMsg,
                    messages,
                    projectId.Value,
                    resolvedContextId,
                    cancellationToken,
                    finalResponseMessages
                )
                .ConfigureAwait(false);

            return new RuntimeAgentExecutionResult(taskIdValue, resolvedContextId, messages);
        }
        catch (Exception exception)
        {
            executionFailure = exception;
            ConversationHistoryPersistenceContext.RecordFailure(exception);
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (turnPersistence is { CompletionAttempted: false })
            {
                try
                {
                    await turnPersistence.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }

            if (session != null && sessionScope != null)
            {
                try
                {
                    await _sessionStateStore.SaveAsync(
                        agent.Type,
                        sessionScope,
                        aiAgent,
                        session,
                        CancellationToken.None
                    );
                }
                catch (Exception exception)
                {
                    if (cleanupFailure == null)
                    {
                        cleanupFailure = exception;
                    }
                    else
                    {
                        _logger.LogError(
                            exception,
                            "A secondary failure occurred while saving an unattended Agent session."
                        );
                    }
                }
            }

            try
            {
                if (aiAgent is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else if (aiAgent is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception exception)
            {
                if (cleanupFailure == null)
                {
                    cleanupFailure = exception;
                }
                else
                {
                    _logger.LogError(exception, "A secondary failure occurred while disposing an unattended Agent.");
                }
            }

            if (cleanupFailure != null)
            {
                if (executionFailure != null)
                {
                    _logger.LogError(
                        cleanupFailure,
                        "Cleanup failed while preserving an unattended Agent execution failure."
                    );
                }
                else
                {
                    ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                }
            }
        }
    }

    internal async Task<List<AgwMessage>> AppendDefinitionSummaryAsync(
        Agent agent,
        IReadOnlyList<ChatMessage> inputMessages,
        IReadOnlyList<AgwMessage> outputMessages,
        Guid projectId,
        string contextId,
        CancellationToken cancellationToken,
        IReadOnlyList<ChatMessage>? finalResponseMessages = null
    )
    {
        if (!agent.EnableSummary)
        {
            return outputMessages.ToList();
        }

        ChatMessage? result;
        if (!string.IsNullOrWhiteSpace(agent.ResponseSchema))
        {
            if (_summaryService is not IAgentStructuredResultService structuredResultService)
            {
                return outputMessages.ToList();
            }

            var finalText = AgentTurnResultText.ExtractLastAssistantText(finalResponseMessages ?? []);
            if (finalText == null)
            {
                return outputMessages.ToList();
            }

            result = await structuredResultService
                .CreateStructuredResultAsync(finalText, projectId, contextId, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var summaryModelProviderId = ResolveSummaryModelProviderId(agent);
            if (!summaryModelProviderId.HasValue)
            {
                return outputMessages.ToList();
            }

            var sourceMessages = new List<ChatMessage>();
            var userText = string.Concat(
                    inputMessages
                        .Where(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)
                        .SelectMany(message => message.Contents)
                        .OfType<Microsoft.Extensions.AI.TextContent>()
                        .Select(content => content.Text)
                )
                .Trim();
            if (!string.IsNullOrWhiteSpace(userText))
            {
                sourceMessages.Add(new ChatMessage(Microsoft.Extensions.AI.ChatRole.User, userText));
            }

            var assistantText = AgentTurnResultText.ExtractLastAssistantText(finalResponseMessages ?? []);
            if (assistantText == null)
            {
                assistantText = string.Concat(
                        outputMessages
                            .SelectMany(message => message.Contents)
                            .OfType<AgwTextContent>()
                            .Select(content => content.Content)
                    )
                    .Trim();
            }
            if (!string.IsNullOrWhiteSpace(assistantText))
            {
                sourceMessages.Add(new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, assistantText.Trim()));
            }

            result = await _summaryService
                .CreateResultAsync(
                    summaryModelProviderId.Value,
                    sourceMessages,
                    projectId,
                    contextId,
                    customInstructions: null,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        var messages = outputMessages.ToList();
        var resultMessage = result.ToAiMessage();
        if (resultMessage != null)
        {
            messages.Add(resultMessage);
        }

        return messages;
    }

    private static Guid? ResolveSummaryModelProviderId(Agent agent) =>
        agent.SummaryModelProviderId ?? (agent.Type == AgentType.System ? agent.ModelProviderId : null);

    internal static async Task<List<AgwMessage>> CollectStreamingMessagesAsync(
        AIAgent aiAgent,
        IReadOnlyList<ChatMessage> chatMessages,
        AgentSession session,
        ToolTurnPersistence turnPersistence,
        CancellationToken cancellationToken,
        IInteractionHandler? approvalHandler = null,
        ICollection<ChatMessage>? finalResponseMessages = null
    )
    {
        var messages = new List<AgwMessage>();
        IEnumerable<ChatMessage> currentMessages = chatMessages;
        approvalHandler ??= new UnattendedInteractionHandler(null);
        for (var round = 0; ; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (round == 32)
            {
                throw new AgwException(ErrorCodes.AgentExecutionFailed, "Tool approval round limit exceeded.");
            }

            var approvals = new List<Microsoft.Extensions.AI.ToolApprovalRequestContent>();
            var responseUpdates = new List<AgentResponseUpdate>();
            await foreach (
                var update in aiAgent.RunStreamingAsync(currentMessages, session, cancellationToken: cancellationToken)
            )
            {
                turnPersistence.Record(ToolStateSnapshots.ToMessage(update));
                responseUpdates.Add(update);
                approvals.AddRange(update.Contents.OfType<Microsoft.Extensions.AI.ToolApprovalRequestContent>());
                if (update.ToAiMessage() is { } message)
                {
                    messages.Add(message);
                }
            }
            if (finalResponseMessages != null)
            {
                finalResponseMessages.Clear();
                foreach (var responseMessage in responseUpdates.ToAgentResponse().Messages)
                {
                    finalResponseMessages.Add(responseMessage);
                }
            }

            if (approvals.Count == 0)
            {
                break;
            }

            var responses = new List<Microsoft.Extensions.AI.AIContent>(approvals.Count);
            foreach (var approval in approvals)
            {
                var request = MafApprovalAdapter.CreateRequest(
                    approval,
                    "standalone",
                    aiAgent.Name,
                    approvalHandler.Requests
                );
                var decision = InteractionResults.RequireResolved(
                    await approvalHandler.ResolveAsync(request, cancellationToken)
                );
                responses.Add(MafApprovalAdapter.CreateResponse(approval, decision));
            }
            currentMessages = [new ChatMessage(Microsoft.Extensions.AI.ChatRole.User, responses)];
        }

        var stateSnapshots = await turnPersistence.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        messages.AddRange(stateSnapshots.Select(static message => message.ToAiMessage()).OfType<AgwMessage>());

        return messages;
    }

    private Task PersistToolBlockMessagesAsync(
        Guid projectId,
        string contextId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken
    )
    {
        return _conversationHistoryWriter == null || messages.Count == 0
            ? Task.CompletedTask
            : _conversationHistoryWriter.AppendAsync(projectId, contextId, messages, cancellationToken);
    }
}
