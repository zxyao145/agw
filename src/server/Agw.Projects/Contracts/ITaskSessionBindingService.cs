using Agw.Shared.Data.Entities.Projects;

namespace Agw.Projects.Application;

public interface ITaskSessionBindingService
{
    /// <summary>
    /// 读取绑定组的生效记录；没有生效记录时返回 null。
    /// Reads the active record of a binding group; returns null when there is no active record.
    /// </summary>
    Task<ProjectConversationBinding?> GetAsync(
        Guid projectId,
        string contextId,
        Guid agentId,
        string externalAgentName,
        CancellationToken cancellationToken = default,
        int expectedGeneration = 0
    );

    /// <summary>
    /// 在绑定组中启用 provider session：相同的生效 ID 保持幂等，新的 ID 归档原记录后创建新记录，已归档的 ID 返回 ConversationSessionConflict。
    /// Enables a provider session in a binding group: the same active ID is idempotent, a new ID archives the previous record and creates a new one, and an archived ID returns ConversationSessionConflict.
    /// </summary>
    Task<ProjectConversationBinding> UpsertAsync(
        Guid projectId,
        string contextId,
        Guid agentId,
        string externalAgentName,
        string providerSessionId,
        string user,
        CancellationToken cancellationToken = default,
        int expectedGeneration = 0
    );

    /// <summary>
    /// 返回对话的全部绑定记录，按创建时间与记录 ID 倒序排列。
    /// Returns every binding record of the conversation, ordered by descending creation time and record ID.
    /// </summary>
    Task<IReadOnlyList<ProjectConversationBinding>> ListAsync(
        Guid projectId,
        Guid conversationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// 归档对话中的指定绑定记录；重复归档保持幂等。
    /// Archives the specified binding record of the conversation; archiving again is idempotent.
    /// </summary>
    Task ArchiveAsync(
        Guid projectId,
        Guid conversationId,
        Guid bindingId,
        CancellationToken cancellationToken = default
    );
}
