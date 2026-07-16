using Agw.Shared.AgwMsgVm;
using Agw.Shared.Extensions;

using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Turns;

internal static class TurnMessageFactory
{
    public static AgwMessage CreateFinished() =>
        new(
            Guid.CreateVersion7().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = "" }],
            new AdditionalPropertiesDictionary
            {
                ["type"] = "turn-finished",
            });
}
