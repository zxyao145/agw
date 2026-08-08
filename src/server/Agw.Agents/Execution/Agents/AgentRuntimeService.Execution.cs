using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Agents.Store;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agents;

public partial class AgentRuntimeService
{
    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentRuntime session,
        AgwUserInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in ExecuteStreamingAsync(
                           session,
                           input,
                           approvalHandler: null,
                           cancellationToken))
        {
            yield return message;
        }
    }

    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentRuntime session,
        AgwUserInput input,
        IHumanGateApprovalHandler? approvalHandler,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            await foreach (var message in session
                               .ExecuteStreamingAsync(input, approvalHandler, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return AgwMessageUtil.PostAgwMessage(session, message);
            }

            yield return TurnMessageFactory.CreateFinished();
        }
        finally
        {
            if (session.SessionStateScope != null)
            {
                await _sessionStateStore.SaveAsync(
                    session.AgentType,
                    session.SessionStateScope,
                    session.Agent,
                    session.Session,
                    CancellationToken.None);
            }
        }
    }

    public async Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgentRuntime session,
        AgwUserInput input,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(session, input, approvalHandler: null, cancellationToken);
    }

    public async Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgentRuntime session,
        AgwUserInput input,
        IHumanGateApprovalHandler? approvalHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            var messages = await session.ExecuteAsync(input, approvalHandler, cancellationToken);
            return messages.Select(message => AgwMessageUtil.PostAgwMessage(session, message)).ToArray();
        }
        finally
        {
            if (session.SessionStateScope != null)
            {
                await _sessionStateStore.SaveAsync(
                    session.AgentType,
                    session.SessionStateScope,
                    session.Agent,
                    session.Session,
                    CancellationToken.None);
            }
        }
    }

    public async Task<AgentExecutionResult?> ExecuteByIdAsync(
        AgentExecuteByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agent = await _agentAppService.GetAgentAsync(request.AgentId);
        if (agent == null)
        {
            return null;
        }

        var req = new AgentExecuteRequest
        {
            Agent = agent,
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
    private async Task<AgentExecutionResult?> ExecuteAsync(
        AgentExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        Guid? taskId = request.TaskId;
        List<ChatMessage> chatMsg = request.Input;
        Guid? projectId = request.ProjectId;
        string? contextId = request.ContextId;
        Agent agent = request.Agent;

        projectId = ProjectDefaults.GetDefaultProjectIdentifier(projectId);
        var resolvedContextId = ContextIdUtil.ResolveContextId(contextId);
        var conversationId = await _sessionStateStore
            .ResolveProjectConversationIdAsync(projectId.Value, resolvedContextId, cancellationToken)
            .ConfigureAwait(false);
        var aiAgent = await CreateAiAgentAsync(new CreateAiAgentRequest
        {
            Agent = agent,
            ProjectId = projectId,
            ConversationId = conversationId ?? Guid.Empty
        }, cancellationToken);
        if (aiAgent == null)
        {
            throw new AgwException(ErrorCodes.AiAgentCreationFailed);
        }

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
                agent.Id);
            session = await _sessionStateStore.GetOrCreateAsync(
                    agent,
                    aiAgent,
                    sessionScope,
                    cancellationToken)
                .ConfigureAwait(false);

            _providerSessionState.InitializeSessionState(
                session,
                resolvedContextId,
                ProjectDefaults.GetDefaultProjectIdentifier(projectId));

            turnPersistence = new ToolTurnPersistence(
                aiAgent,
                session,
                (messages, token) => PersistToolBlockMessagesAsync(
                    projectId.Value,
                    resolvedContextId,
                    messages,
                    token));
            var messages = await CollectStreamingMessagesAsync(
                    aiAgent,
                    chatMsg,
                    session,
                    turnPersistence,
                    cancellationToken)
                .ConfigureAwait(false);
            messages = await AppendDefinitionSummaryAsync(
                agent,
                chatMsg,
                messages,
                projectId.Value,
                resolvedContextId,
                cancellationToken)
                .ConfigureAwait(false);

            return new AgentExecutionResult(taskIdValue, resolvedContextId, messages);
        }
        catch (Exception exception)
        {
            executionFailure = exception;
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (turnPersistence is { CompletionAttempted: false })
            {
                try
                {
                    await turnPersistence
                        .CompleteAsync(CancellationToken.None)
                        .ConfigureAwait(false);
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
                        CancellationToken.None);
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
                            "A secondary failure occurred while saving an unattended Agent session.");
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
                    _logger.LogError(
                        exception,
                        "A secondary failure occurred while disposing an unattended Agent.");
                }
            }

            if (cleanupFailure != null)
            {
                if (executionFailure != null)
                {
                    _logger.LogError(
                        cleanupFailure,
                        "Cleanup failed while preserving an unattended Agent execution failure.");
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
        CancellationToken cancellationToken)
    {
        var summaryModelProviderId = ResolveSummaryModelProviderId(agent);
        if (!agent.EnableSummary ||
            !summaryModelProviderId.HasValue)
        {
            return outputMessages.ToList();
        }

        var sourceMessages = new List<ChatMessage>();
        var userText = string.Concat(
                inputMessages
                    .Where(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)
                    .SelectMany(message => message.Contents)
                    .OfType<Microsoft.Extensions.AI.TextContent>()
                    .Select(content => content.Text))
            .Trim();
        if (!string.IsNullOrWhiteSpace(userText))
        {
            sourceMessages.Add(new ChatMessage(Microsoft.Extensions.AI.ChatRole.User, userText));
        }

        var assistantText = string.Concat(
                outputMessages
                    .SelectMany(message => message.Contents)
                    .OfType<AgwTextContent>()
                    .Select(content => content.Content))
            .Trim();
        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            sourceMessages.Add(new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, assistantText));
        }

        var result = await _summaryService.CreateResultAsync(
            summaryModelProviderId.Value,
            sourceMessages,
            projectId,
            contextId,
            customInstructions: null,
            cancellationToken)
            .ConfigureAwait(false);
        var messages = outputMessages.ToList();
        var resultMessage = result.ToAiMessage();
        if (resultMessage != null)
        {
            messages.Add(resultMessage);
        }

        return messages;
    }

    private static Guid? ResolveSummaryModelProviderId(Agent agent) =>
        agent.SummaryModelProviderId ??
        (agent.Type == AgentType.System ? agent.ModelProviderId : null);

    internal static async Task<List<AgwMessage>> CollectStreamingMessagesAsync(
        AIAgent aiAgent,
        IReadOnlyList<ChatMessage> chatMessages,
        AgentSession session,
        ToolTurnPersistence turnPersistence,
        CancellationToken cancellationToken)
    {
        var stream = aiAgent.RunStreamingAsync(chatMessages, session, cancellationToken: cancellationToken);
        var messages = new List<AgwMessage>();
        await foreach (var update in stream)
        {
            turnPersistence.Record(ToolStateSnapshots.ToMessage(update));
            if (update.Contents.OfType<Microsoft.Extensions.AI.ToolApprovalRequestContent>().Any())
            {
                throw new AgwException(
                    ErrorCodes.AgentExecutionFailed,
                    "Tool approval cannot be requested during unattended Agent execution.");
            }

            var msg = update.ToAiMessage();
            if (msg != null)
            {
                messages.Add(msg);
            }
        }

        var stateSnapshots = await turnPersistence
            .CompleteAsync(CancellationToken.None)
            .ConfigureAwait(false);
        messages.AddRange(
            stateSnapshots
                .Select(static message => message.ToAiMessage())
                .OfType<AgwMessage>());

        return messages;
    }

    private Task PersistToolBlockMessagesAsync(
        Guid projectId,
        string contextId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        return _conversationHistoryWriter == null || messages.Count == 0
            ? Task.CompletedTask
            : _conversationHistoryWriter.AppendAsync(
                projectId,
                contextId,
                messages,
                cancellationToken);
    }
}
