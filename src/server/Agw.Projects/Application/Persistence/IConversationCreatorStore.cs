using Agw.Shared.Data.Entities.Projects;

namespace Agw.Projects.Application.Persistence;

/// <summary>
/// 在对话行锁内记录开放对话的创建方：取得项目锁与对话行锁后重新读取对话及是否有已受理的 Turn，执行 Application 提供的操作委托，再保存创建方并提交。
/// Records the creator of an open conversation under the conversation row lock: after taking the project and conversation row locks, re-reads the conversation and whether it has accepted turns, runs the operation delegate supplied by Application, then saves the creator and commits.
/// </summary>
public interface IConversationCreatorStore
{
    /// <summary>
    /// Generation 不匹配或对话不存在时返回 ConversationSessionConflict；操作抛出异常时回滚，创建方保持不变。
    /// A generation mismatch or a missing conversation returns ConversationSessionConflict; an exception from the operation rolls back and leaves the creator unchanged.
    /// </summary>
    Task ClaimOpenConversationAsync(
        Guid conversationId,
        int expectedGeneration,
        Action<ConversationCreatorClaim> operation,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// 锁内读取的对话及其是否有已受理的 Turn。The conversation read under the locks and whether it has accepted turns.
/// </summary>
public sealed record ConversationCreatorClaim(ProjectConversation Conversation, bool HasAcceptedTurns);
