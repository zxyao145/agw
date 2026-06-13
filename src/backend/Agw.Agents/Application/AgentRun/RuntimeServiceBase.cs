using Agw.Shared.AgwMsgVm;
using Agw.Shared.Extensions;

using Microsoft.Extensions.AI;

namespace Agw.Agents.Application.AgentRun;

public class RuntimeServiceBase
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
