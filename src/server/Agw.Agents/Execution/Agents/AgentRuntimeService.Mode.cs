using Agw.Agents.Execution.Runtimes;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Exceptions;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents;

public partial class AgentRuntimeService
{
    public async Task SetModeAsync(
        AgentRuntime runtime,
        string mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var modeProvider = runtime.Agent.GetService<AgentModeProvider>()
            ?? throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Agent '{runtime.Agent.Name}' does not support mode changes.");

        await modeProvider.SetModeAsync(runtime.Session, mode, cancellationToken);
        if (runtime.SessionStateScope != null)
        {
            await _sessionStateStore.SaveAsync(
                runtime.AgentType,
                runtime.SessionStateScope,
                runtime.Agent,
                runtime.Session,
                cancellationToken);
        }

        if (_conversationHistoryWriter != null)
        {
            await _conversationHistoryWriter.AppendAsync(
                runtime._projectId,
                runtime._contextId,
                [CreateModeStatusMessage(mode)],
                cancellationToken);
        }
    }

    internal static ChatMessage CreateModeStatusMessage(string mode) =>
        new(ChatRole.System, [new TextContent(string.Empty)])
        {
            MessageId = Guid.CreateVersion7().ToString("N"),
            AuthorName = "tools",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["type"] = ToolMessageTypes.ModeStatus,
                ["mode"] = mode,
                ["presentation"] = "control",
            },
        };
}
