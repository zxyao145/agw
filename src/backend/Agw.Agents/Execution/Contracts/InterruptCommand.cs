namespace Agw.Agents.Execution.Contracts;

public class InterruptCommand : AgentRunCommand
{
    public string? Reason { get; set; }
}
