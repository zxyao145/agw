using Agw.Shared.Data.Entities.Projects;

namespace Agw.Projects.Domain.Rules;

public static class ProjectConversationChatHistoryRules
{
    public static IReadOnlyList<ProjectConversationChatHistory> Order(
        IEnumerable<ProjectConversationChatHistory> records
    ) =>
        records
            .OrderBy(r => r.ConversationSequence ?? long.MinValue)
            .ThenBy(r => r.CreateTime)
            .ThenBy(r => r.UpdateTime ?? r.CreateTime)
            .ToList();

    public static ProjectConversationChatHistory? GetLatest(IEnumerable<ProjectConversationChatHistory> records) =>
        Order(records).LastOrDefault();
}
