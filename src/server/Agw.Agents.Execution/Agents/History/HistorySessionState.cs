using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Agw.Projects.Contracts.History;
using Agw.Shared.Runtime;
using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Agents.History;

/// <summary>
/// 会话里保存的历史归属：项目、对话、历史作用域与节点名称；Generation 与执行绑定取自当前执行身份。
/// The history ownership kept in the session: project, conversation, history scope and node name; generation and execution binding come from the current execution identity.
/// </summary>
public sealed class HistorySessionState : IProviderSessionState
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
    private readonly ProviderSessionState<State> _state = new(
        _ => new State(ContextIdUtil.ResolveContextId(null), ProjectDefaults.DefaultBuiltInId),
        nameof(AgwChatHistoryProvider),
        JsonOptions
    );

    public string StateKey => _state.StateKey;

    public void InitializeSessionState(AgentSession session, string contextId, Guid projectId)
    {
        ArgumentNullException.ThrowIfNull(session);
        _state.SaveState(session, new State(ContextIdUtil.NormalizeContextId(contextId), projectId));
    }

    public void InitializeSessionState(AgentSession session, string contextId, Guid projectId, string historyScope) =>
        InitializeSessionState(session, contextId, projectId, historyScope, nodeName: null);

    public void InitializeSessionState(
        AgentSession session,
        string contextId,
        Guid projectId,
        string historyScope,
        string? nodeName
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        _state.SaveState(
            session,
            new State(ContextIdUtil.NormalizeContextId(contextId), projectId, historyScope.Trim(), nodeName)
        );
    }

    public ConversationMessageWriteScope GetMessageWriteScope(AgentSession session)
    {
        var state = Get(session);
        return new ConversationMessageWriteScope
        {
            ProjectId = state.ProjectId,
            ContextId = state.ContextId,
            Generation = state.Generation,
            ProducerId = Guid.CreateVersion7(),
            HistoryScope = state.HistoryScope,
            NodeName = state.NodeName,
            IsExecutionBound = state.IsExecutionBound,
        };
    }

    public bool TryGetProjectContext(AgentSession session, out Guid projectId, out string contextId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.StateBag.TryGetValue(_state.StateKey, out State? state, JsonOptions) || state == null)
        {
            projectId = Guid.Empty;
            contextId = string.Empty;
            return false;
        }

        projectId = state.ProjectId;
        contextId = state.ContextId;
        return true;
    }

    internal State Get(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _state.GetOrInitializeState(session);
    }

    internal sealed record State
    {
        public State(string contextId, Guid projectId, string? historyScope = null, string? nodeName = null)
        {
            ContextId = contextId;
            var execution = ExecutionContextSlot.FindBound(projectId, contextId);
            Generation = execution?.Generation ?? 0;
            IsExecutionBound = execution != null;
            ProjectId = projectId;
            HistoryScope = historyScope;
            NodeName = string.IsNullOrWhiteSpace(nodeName) ? null : nodeName.Trim();
        }

        public int Generation { get; init; }

        public bool IsExecutionBound { get; init; }

        public string ContextId { get; init; }

        public Guid ProjectId { get; init; }

        public string? HistoryScope { get; init; }

        public string? NodeName { get; init; }

        public ConversationHistoryScope Conversation =>
            new()
            {
                ProjectId = ProjectId,
                ContextId = ContextId,
                Generation = Generation,
                IsExecutionBound = IsExecutionBound,
            };
    }
}
