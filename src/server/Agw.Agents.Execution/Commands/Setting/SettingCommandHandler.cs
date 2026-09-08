using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Connections;

namespace Agw.Agents.Execution.Commands.Setting;

public sealed class SettingCommandHandler : IExecutionCommandHandler<SettingCommand>
{
    public Task HandleAsync(
        SettingCommand command,
        ExecutionConnectionContext context,
        CancellationToken cancellationToken
    ) => context.ApplySettingsAsync(ExecutionSettings.FromCommand(command), cancellationToken);
}
