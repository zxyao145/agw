using Agw.Shared.AgwMsgVm;
using Agw.Shared.Extensions;

using Microsoft.Extensions.AI;

namespace Agw.Agents.Runtime;

public class RuntimeServiceBase
{
    protected static AgwMessage CreateTurnFinishedMessage(CancellationToken cancellationToken)
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { "type", "turn-finished" },
        };

        var content = new AgwTextContent
        {
            Content = "",
        };

        var payload = new AgwMessage(
            Guid.NewGuid().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [content],
            additionalProperties);
        return payload;
    }
}
