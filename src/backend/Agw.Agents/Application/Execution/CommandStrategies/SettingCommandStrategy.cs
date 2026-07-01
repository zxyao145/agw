using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Contracts;
using Agw.Shared.Contracts.Tasks;

using Microsoft.Extensions.Logging;

namespace Agw.Agents.Application.Execution.CommandStrategies;

internal sealed class SettingCommandStrategy : IExecutionCommandStrategy
{
    private readonly ITaskAppService _taskAppService;
    private readonly ILogger<SettingCommandStrategy> _logger;

    public SettingCommandStrategy(ITaskAppService taskAppService, ILogger<SettingCommandStrategy> logger)
    {
        _taskAppService = taskAppService;
        _logger = logger;
    }

    public async Task<SettingCommand> NormalizeSettingsAsync(SettingCommand settings, CancellationToken cancellationToken)
    {
        var normalizedSettings = new SettingCommand(
            settings.ProjectId,
            settings.TaskId,
            new Dictionary<string, string>(settings.EnvironmentVariables),
            settings.ContextId);
        if (await _taskAppService.HasTaskAsync(normalizedSettings.TaskId, cancellationToken: cancellationToken))
        {
            normalizedSettings.Resume = true;
        }

        return normalizedSettings;
    }

    public bool CanHandle(AgentRunCommand command) => command is SettingCommand;

    public async Task<ExecutionCommandResult> ExecuteAsync(
        AgentRunCommand command,
        ExecutionCommandContext context)
    {
        var settingCommand = (SettingCommand)command;
        var normalizedSettings = await NormalizeSettingsAsync(
            settingCommand,
            context.CancellationToken);
        context.ConnectionState.ApplySettings(normalizedSettings);

        if (!context.ConnectionState.ShouldRefreshSessionImmediately)
        {
            return default;
        }

        context.ConnectionState.ClearSession();
        context.AgentSession = await DisposeSessionAsync(context.AgentSession);
        return default;
    }

    private static async Task<AgentExecSession?> DisposeSessionAsync(AgentExecSession? agentSession)
    {
        if (agentSession == null)
        {
            return null;
        }

        agentSession.CancelActiveRequest();
        await agentSession.DisposeAsync();
        return null;
    }
}
