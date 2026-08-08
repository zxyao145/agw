using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Data;

namespace Agw.Agents.Execution.Commands.Exec;

public class ExecCommand : AgentRunCommand
{
    [JsonConstructor]
    [SetsRequiredMembers]
    public ExecCommand(
        AgentRuntimeType agentType,
        AgwUserInput input)
    {
        AgentType = agentType;
        Input = input;
    }

    public AgentRuntimeType AgentType { get; set; }

    public Guid? AgentId { get; set; }

    public bool Stream { get; set; } = true;

    public AgwUserInput Input { get; set; }
}
