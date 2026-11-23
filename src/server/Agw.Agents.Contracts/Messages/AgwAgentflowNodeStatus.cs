namespace Agw.Agents.Contracts.Messages;

/// <summary>
/// agentflow-node-status 的 status 取值。
/// The status values of agentflow-node-status.
/// </summary>
public static class AgwAgentflowNodeStatus
{
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Completed = "completed";
    public const string Failed = "failed";
}
