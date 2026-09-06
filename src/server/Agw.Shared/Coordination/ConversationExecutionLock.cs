namespace Agw.Shared.Coordination;

public static class ConversationExecutionLock
{
    public static string GetResourceName(Guid conversationId) => $"conversation-execution:{conversationId:D}";
}
