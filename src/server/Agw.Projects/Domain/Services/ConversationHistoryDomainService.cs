using Agw.Shared.Data.Entities.Projects;

namespace Agw.Projects.Domain.Services;

/// <summary>
/// <para>跨越单条历史记录、作用于同一会话下多条记录集合的领域规则。</para>
/// <para>Domain rules that span the history records of a conversation instead of binding to a single history root.</para>
/// </summary>
public sealed class ConversationHistoryDomainService
{
    public IReadOnlyList<ProjectConversationChatHistory> Order(IEnumerable<ProjectConversationChatHistory> records) =>
        records
            .OrderBy(record => record.ConversationSequence ?? long.MinValue)
            .ThenBy(record => record.CreateTime)
            .ThenBy(record => record.UpdateTime ?? record.CreateTime)
            .ToList();

    public ProjectConversationChatHistory? GetLatest(IEnumerable<ProjectConversationChatHistory> records) =>
        Order(records).LastOrDefault();
}
