using Agw.Shared.Data.Entities.Projects;

namespace Agw.Projects.Application.Persistence;

/// <summary>
/// 协调 provider session 绑定的保存与归档：每次操作使用独立 scope、数据库写入锁与事务，加载目标绑定组的全部记录后执行 Application 提供的操作委托。
/// Coordinates saving and archiving provider session bindings: each operation uses its own scope, database write locks and transaction, and runs the operation delegate supplied by Application after loading every record of the target binding group.
/// </summary>
public interface IProjectProviderSessionCoordinator
{
    /// <summary>
    /// 在调用方已持有项目锁与 Agent 定义锁时保存绑定组；Generation 不匹配时返回 ConversationSessionConflict。
    /// Saves a binding group while the caller holds the project and Agent definition locks; a generation mismatch returns ConversationSessionConflict.
    /// </summary>
    Task<TResult> SaveAsync<TResult>(
        ProviderSessionSaveTarget target,
        Func<ProviderSessionOperation, Task<TResult>> operation,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// 依次取得对话执行锁、项目锁与 Agent 定义锁后归档指定记录；记录已经归档时直接返回，对话存在活动执行时返回 ConversationSessionConflict。
    /// Archives the specified record after acquiring the conversation execution, project and Agent definition locks in turn; an archived record returns at once, and an active conversation execution returns ConversationSessionConflict.
    /// </summary>
    Task ArchiveAsync(
        ProviderSessionArchiveTarget target,
        Func<ProviderSessionOperation, Task> operation,
        CancellationToken cancellationToken = default
    );
}

public sealed record ProviderSessionSaveTarget(
    Guid ProjectId,
    Guid ConversationId,
    int ExpectedGeneration,
    Guid AgentId,
    string ExternalAgentName
);

public sealed record ProviderSessionArchiveTarget(Guid ProjectId, Guid ConversationId, Guid BindingId);

/// <summary>
/// 一次操作的数据：锁内加载的对话及其目标绑定组、关联各锁的取消令牌，以及绑定到本次操作 scope 与事务的保存委托。
/// The data of one operation: the conversation loaded under the locks with its target binding group, the cancellation token linked to the locks, and the save delegate bound to this operation's scope and transaction.
/// </summary>
public sealed record ProviderSessionOperation(
    ProjectConversation Conversation,
    CancellationToken CancellationToken,
    Func<CancellationToken, Task> SaveChangesAsync
);
