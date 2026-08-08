using Agw.Agents.Execution.Commands.Abstracts;

namespace Agw.Agents.Execution.Commands.Interrupt;

public class InterruptCommand : AgentRunCommand
{
    public string? Reason { get; set; }
}
