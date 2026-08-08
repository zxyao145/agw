using Agw.Agents.Execution.Commands.Abstracts;

namespace Agw.Agents.Execution.Commands.Mode;

public sealed class SetModeCommand : AgentRunCommand
{
    public Guid AgentId { get; set; }

    public string Mode { get; set; } = string.Empty;
}
