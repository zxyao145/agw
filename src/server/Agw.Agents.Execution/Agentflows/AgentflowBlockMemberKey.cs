namespace Agw.Agents.Execution.Agentflows;

internal static class AgentflowBlockMemberKey
{
    internal static string Create(string nodeId, string memberId) => $"{nodeId}.{memberId}";
}
