namespace Agw.Projects.Contracts.Execution;

/// <summary>
/// 写入操作之前检查请求方能否写入对话：宿主可以写入任何对话，extension 只能写入自己创建的对话、开放对话与还没有保存的对话。
/// Checks before a write whether the requester may write a conversation: the host may write any conversation, an extension only its own, open and not yet saved conversations.
/// </summary>
public interface IProjectConversationAccessFacade
{
    /// <summary>
    /// 不能写入时抛出 ConversationReadOnly；requesterExtensionId 为 null 表示宿主。
    /// Throws ConversationReadOnly when the write is not allowed; a null requesterExtensionId means the host.
    /// </summary>
    Task EnsureWritableAsync(
        Guid conversationId,
        string? requesterExtensionId,
        CancellationToken cancellationToken = default
    );
}
