using Agw.Shared.Models;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Text;

namespace Agw.Agents.Application;

public class RuntimService
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
            Guid.NewGuid().ToString(),
            "$agw-server",
            AiRole.System,
            new List<AgwContent> { content });
        return payload;
    }
}
