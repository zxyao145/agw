namespace Agw.Shared.Contracts.Conversations;

/// <summary>
/// 对话创建方的保留值；其他值是创建对话的 Desktop extension 的 id。
/// Reserved conversation creator values; any other value is the id of the Desktop extension that created the conversation.
/// </summary>
public static class ConversationCreators
{
    /// <summary>
    /// 宿主（Desktop、Web、Mobile、Jobs、A2A）创建的对话。extension 的 id 不能使用这个值。
    /// A conversation created by the host (Desktop, Web, Mobile, Jobs, A2A). Extension ids cannot use this value.
    /// </summary>
    public const string Host = "host";
}
