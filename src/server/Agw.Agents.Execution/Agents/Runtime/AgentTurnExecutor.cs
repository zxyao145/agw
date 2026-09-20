using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Turns;
using Microsoft.Agents.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agents.Runtime;

public sealed class AgentTurnExecutor
{
    private readonly ChatHistoryProvider _chatHistoryProvider;
    private readonly AgentSessionStateStore _sessionStateStore;
    private readonly IConversationHandoffProvider _conversationHandoffProvider;

    public AgentTurnExecutor(
        ChatHistoryProvider chatHistoryProvider,
        AgentSessionStateStore sessionStateStore,
        IConversationHandoffProvider conversationHandoffProvider
    )
    {
        _chatHistoryProvider = chatHistoryProvider;
        _sessionStateStore = sessionStateStore;
        _conversationHandoffProvider = conversationHandoffProvider;
    }

    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentRuntime session,
        AgwUserInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await foreach (var message in ExecuteStreamingAsync(session, input, approvalHandler: null, cancellationToken))
        {
            yield return message;
        }
    }

    public IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentRuntime session,
        AgwUserInput input,
        IInteractionHandler? approvalHandler,
        CancellationToken cancellationToken = default
    ) =>
        ConversationHistoryPersistenceContext.RunStreaming(
            ExecuteStreamingWithHistoryAsync(session, input, approvalHandler, cancellationToken),
            _chatHistoryProvider as IConversationHistoryPersistence,
            session._projectId,
            session._contextId,
            session.SessionStateScope?.Generation
                ?? ConversationSessionContext.GetGeneration(session._projectId, session._contextId),
            allowCreateConversation: session.SessionStateScope?.ProjectConversationId == Guid.Empty
        );

    private async IAsyncEnumerable<AgwMessage> ExecuteStreamingWithHistoryAsync(
        AgentRuntime session,
        AgwUserInput input,
        IInteractionHandler? approvalHandler,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            var requestMessages = await CreateExecutionInputMessagesAsync(session, input, cancellationToken)
                .ConfigureAwait(false);
            await foreach (
                var message in ConversationHistoryPersistenceContext
                    .ObserveAsync(
                        session.ExecuteStreamingAsync(requestMessages, input, approvalHandler, cancellationToken),
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
            if (session.SessionStateScope != null)
            {
                await _sessionStateStore.SaveAsync(
                    session.AgentType,
                    session.SessionStateScope,
                    session.Agent,
                    session.Session,
                    CancellationToken.None
                );
            }
        }
        await ConversationHistoryPersistenceContext.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        yield return TurnMessageFactory.CreateFinished();
    }

    public async Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgentRuntime session,
        AgwUserInput input,
        CancellationToken cancellationToken = default
    )
    {
        return await ExecuteAsync(session, input, approvalHandler: null, cancellationToken);
    }

    /// <summary>
    /// 使用恢复后的 MAF session 执行 approval 响应分段，并在结束时保存 Agent session。
    /// </summary>
    internal IAsyncEnumerable<AgwMessage> ExecuteDurableSegmentStreamingAsync(
        AgentRuntime session,
        ChatMessage message,
        AgwUserInput summaryInput,
        IInteractionHandler approvalHandler,
        CancellationToken cancellationToken = default
    ) =>
        ConversationHistoryPersistenceContext.RunStreaming(
            ExecuteDurableSegmentWithHistoryAsync(session, message, summaryInput, approvalHandler, cancellationToken),
            _chatHistoryProvider as IConversationHistoryPersistence,
            session._projectId,
            session._contextId,
            session.SessionStateScope?.Generation
                ?? ConversationSessionContext.GetGeneration(session._projectId, session._contextId)
        );

    private async IAsyncEnumerable<AgwMessage> ExecuteDurableSegmentWithHistoryAsync(
        AgentRuntime session,
        ChatMessage message,
        AgwUserInput summaryInput,
        IInteractionHandler approvalHandler,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(summaryInput);
        ArgumentNullException.ThrowIfNull(approvalHandler);

        try
        {
            await foreach (
                var output in ConversationHistoryPersistenceContext
                    .ObserveAsync(
                        session.ExecuteStreamingSegmentAsync(message, summaryInput, approvalHandler, cancellationToken),
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            )
            {
                yield return output;
            }
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
                    CancellationToken.None
                );
            }
        }
    }

    public async Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgentRuntime session,
        AgwUserInput input,
        IInteractionHandler? approvalHandler,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            var requestMessages = await CreateExecutionInputMessagesAsync(session, input, cancellationToken)
                .ConfigureAwait(false);
            var messages = await ConversationHistoryPersistenceContext
                .ObserveAsync(session.ExecuteAsync(requestMessages, input, approvalHandler, cancellationToken))
                .ConfigureAwait(false);
            return messages;
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
                    CancellationToken.None
                );
            }
        }
    }

    private async Task<List<ChatMessage>> CreateExecutionInputMessagesAsync(
        AgentRuntime session,
        AgwUserInput input,
        CancellationToken cancellationToken
    )
    {
        var sessionScope = session.SessionStateScope;
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
}
