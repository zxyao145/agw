using Agw.Agents.Execution.Agents.Store;
using Agw.Agents.Execution.Turns;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agentflows;

/// <summary>
/// 准备执行会话与初始消息，统一 Conversation 解析和 Handoff 上下文加载。
/// </summary>
public sealed class AgentflowExecutionContextFactory
{
    private readonly IProviderSessionState _providerSessionState;
    private readonly AgentSessionStateStore? _sessionStateStore;
    private readonly IConversationHistoryWriter? _conversationHistoryWriter;
    private readonly IConversationHandoffProvider? _conversationHandoffProvider;

    public AgentflowExecutionContextFactory(
        IProviderSessionState providerSessionState,
        AgentSessionStateStore? sessionStateStore = null,
        IConversationHistoryWriter? conversationHistoryWriter = null,
        IConversationHandoffProvider? conversationHandoffProvider = null
    )
    {
        _providerSessionState = providerSessionState;
        _sessionStateStore = sessionStateStore;
        _conversationHistoryWriter = conversationHistoryWriter;
        _conversationHandoffProvider = conversationHandoffProvider;
    }

    internal static AgwUserInput CreateUserInput(string input) =>
        new() { Author = Constants.DefaultInputAuthor, Contents = [new AgwTextContent { Content = input }] };

    internal async Task<AgentflowAgentSessionScope> CreateSessionScopeAsync(
        Guid projectId,
        string contextId,
        Guid? taskId,
        Guid? conversationId,
        CancellationToken cancellationToken,
        PermissionModeState permissionState
    )
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
            permissionState
        );
    }

    private async Task<Guid> ResolveProjectConversationIdAsync(
        Guid projectId,
        string contextId,
        CancellationToken cancellationToken
    )
    {
        if (_sessionStateStore == null)
        {
            return Guid.Empty;
        }

        return await _sessionStateStore
                .ResolveProjectConversationIdAsync(projectId, contextId, cancellationToken)
                .ConfigureAwait(false)
            ?? Guid.Empty;
    }

    internal async Task<List<ChatMessage>> CreateWorkflowInputMessagesAsync(
        Guid agentflowId,
        Guid conversationId,
        AgwUserInput input,
        CancellationToken cancellationToken
    )
    {
        var handoff =
            _conversationHandoffProvider == null
                ? ConversationHandoff.Empty
                : await _conversationHandoffProvider
                    .CreateAsync(conversationId, AgentRuntimeType.Agentflow, agentflowId, cancellationToken)
                    .ConfigureAwait(false);
        return AgwMessageUtil.CreateExecutionInputMessages(input, AgentRuntimeType.Agentflow, agentflowId, handoff);
    }
}
