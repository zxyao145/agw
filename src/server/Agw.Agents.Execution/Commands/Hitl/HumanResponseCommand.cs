using System.Text.Json.Serialization;
using Agw.Agents.Execution.Commands.Abstracts;

namespace Agw.Agents.Execution.Commands.Hitl;

public class HumanResponseCommand : AgentRunCommand
{
    [JsonConstructor]
    public HumanResponseCommand(InteractionResponse response, Guid? turnId = null)
    {
        Response = response;
        TurnId = turnId;
    }

    public InteractionResponse Response { get; set; }
    public Guid? TurnId { get; set; }
}
