using Agw.Agents.Contracts;

namespace Agw.Agents.Application.Execution.CommandStrategies;

internal sealed class SettingCommandStrategy : IExecutionCommandStrategy
{
    public Task<SettingCommand> NormalizeSettingsAsync(SettingCommand settings, CancellationToken cancellationToken)
    {
        var normalizedSettings = new SettingCommand(
            settings.ProjectId,
            new Dictionary<string, string>(settings.EnvironmentVariables),
            settings.ContextId)
        {
            Resume = settings.Resume
        };

        return Task.FromResult(normalizedSettings);
    }

    public bool CanHandle(AgentRunCommand command) => command is SettingCommand;

    public async Task<ExecutionCommandResult> ExecuteAsync(
        AgentRunCommand command,
        ExecutionCommandContext context)
    {
        var settingCommand = (SettingCommand)command;
        if (context.RuntimeSession is { HasActiveTurn: true })
        {
            await context.SendErrorAsync("The previous session is currently in progress, please wait and execute again.");
            return default;
        }

        var normalizedSettings = await NormalizeSettingsAsync(
            settingCommand,
            context.CancellationToken);
        context.ConnectionState.ApplySettings(normalizedSettings);

        if (!context.ConnectionState.ShouldRefreshSessionImmediately)
        {
            return default;
        }

        context.ConnectionState.ClearSession();
        context.RuntimeSession = await ExecutionRuntimeStarter.DisposeSessionAsync(context.RuntimeSession);
        return default;
    }
}
