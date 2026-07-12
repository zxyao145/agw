using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Contracts;

namespace Agw.Agents.Execution.Commands;

public sealed class SettingCommandHandler : ExecutionCommandHandler<SettingCommand>
{
    protected override async Task HandleAsync(
        SettingCommand command,
        ExecutionConnection connection,
        CancellationToken cancellationToken)
    {
        if (connection.Runtime is { HasActiveTurn: true })
        {
            await connection.SendErrorAsync(ExecutionConnection.BusyMessage);
            return;
        }

        var normalized = Clone(command);
        if (connection.Settings == normalized)
        {
            return;
        }

        await connection.ReplaceRuntimeAsync();
        connection.Settings = normalized;
        connection.ResolvedTask = null;
        connection.Target = null;
    }

    internal static SettingCommand Clone(SettingCommand settings) =>
        new(
            settings.ProjectId,
            new Dictionary<string, string>(settings.EnvironmentVariables),
            settings.ContextId)
        {
            Resume = settings.Resume,
        };
}
