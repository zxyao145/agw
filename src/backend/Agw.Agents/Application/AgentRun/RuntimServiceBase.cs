using Agw.Shared.Extensions;
using Agw.Shared.Models;

using Microsoft.Extensions.AI;

namespace Agw.Agents.Application.AgentRun;

public class RuntimServiceBase
{
    protected static AgwMessage CreateTurnFinishedMessage(CancellationToken cancellationToken)
    {
        var content = new AgwTextContent
        {
            Content = "",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                { "type", "turn-finished" },
                { "status", "" }
            }
        };

        var payload = new AgwMessage(
            Guid.NewGuid().Normalize(),
            "$agw-server",
            AiRole.System,
            new List<AgwContent> { content });
        return payload;
    }
}
