using System.Text.Json.Serialization;
using Agw.Agents.Execution.Commands.Abstracts;

namespace Agw.Agents.Execution.Commands.Hitl;

public class HumanResponseCommand : AgentRunCommand
{
    [JsonConstructor]
    public HumanResponseCommand(InteractionResponse response, Guid? executionId = null)
    {
        Response = response;
        ExecutionId = executionId;
    }

    public InteractionResponse Response { get; set; }
    public Guid? ExecutionId { get; set; }
}
