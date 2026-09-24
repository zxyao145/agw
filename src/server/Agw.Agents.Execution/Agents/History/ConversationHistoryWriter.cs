using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Context;
using Agw.Projects.Contracts.History;
using Agw.Shared.Runtime;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.History;

/// <summary>
/// 按消息身份写入完整消息：行 Id 等于 MessageId，重复写入更新同一行。Turn 内的写入经本 Turn 唯一的写入者进入历史缓冲，Turn 之外直接写入。
/// Writes complete messages by message identity: the row Id equals the MessageId and repeated writes update the same row. Writes inside a turn go through that turn's single writer into the history buffer; writes outside a turn go directly.
/// </summary>
internal sealed class ConversationHistoryWriter : IConversationHistoryWriter
{
    private readonly IConversationHistoryStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ConditionalWeakTable<IConversationHistoryBuffer, HistoryRecording> _turnWriters = new();

    public ConversationHistoryWriter(IConversationHistoryStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task AppendAsync(
        Guid projectId,
        string contextId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(messages);
        contextId = ContextIdUtil.NormalizeContextId(contextId);
        var scope = ExecutionScope.Current;
        var recording =
            scope?.History is { } buffer
            && buffer.Scope.ProjectId == projectId
            && string.Equals(buffer.Scope.ContextId, contextId, StringComparison.Ordinal)
                ? _turnWriters.GetValue(buffer, _ => CreateRecording(projectId, contextId, buffer.Scope, scope))
                : CreateRecording(projectId, contextId, conversation: null, scope: null);
        await recording.WriteMessagesAsync(messages, null, cancellationToken).ConfigureAwait(false);
    }

    private HistoryRecording CreateRecording(
        Guid projectId,
        string contextId,
        ConversationHistoryScope? conversation,
        ExecutionScope? scope
    )
    {
        var execution = conversation == null ? ExecutionContextSlot.FindBound(projectId, contextId) : null;
        return new HistoryRecording(
            new ConversationMessageWriteScope
            {
                ProjectId = projectId,
                ContextId = contextId,
                Generation = conversation?.Generation ?? execution?.Generation ?? 0,
                ProducerId = Guid.CreateVersion7(),
                TurnId = scope?.Context.TurnId,
                AgentId = scope?.Context.AgentId,
                IsExecutionBound = conversation?.IsExecutionBound ?? execution != null,
            },
            _store,
            new ModelMessageAdapter(),
            _timeProvider,
            agentName: null,
            transient: false,
            structuredResult: false
        );
    }
}
