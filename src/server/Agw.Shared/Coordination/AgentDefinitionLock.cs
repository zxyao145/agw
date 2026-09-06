namespace Agw.Shared.Coordination;

public static class AgentDefinitionLock
{
    public static string GetResourceName(string ownerUserId) => $"agent-definitions:{ownerUserId}";
}
