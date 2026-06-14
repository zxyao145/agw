using Agw.Shared.AgwMsgVm;
using Agw.Shared.Extensions;

using Microsoft.Extensions.AI;

namespace Agw.Agents.Application.AgentRun;

public class RuntimeServiceBase
{
    protected static AgwMessage CreateResultMessage(
        string content = "",
        CancellationToken cancellationToken = default
    )
    {
        var contents = new List<AgwContent>
        {
            new AgwTextContent
            {
                Content = content,
            }
        };

        return CreateResultMessage(contents, cancellationToken);
    }


    // ReSharper disable once MemberCanBePrivate.Global
    protected static AgwMessage CreateResultMessage(
        List<AgwContent> contents,
        CancellationToken cancellationToken = default
    )
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { "type", "result" },
        };

        var payload = new AgwMessage(
            Guid.NewGuid().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            contents,
            additionalProperties
        );

        return payload;
    }


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
