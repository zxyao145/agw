using System.Text.Json;

using Agw.Agents.Application;
using Agw.Api.Contracts;

namespace Agw.Api.Execution;

internal sealed class SettingCommandStrategy : IExecutionCommandStrategy
{
    public bool CanHandle(AgentRunCommand command) => command is SettingCommand;

    public async Task<ExecutionCommandResult> ExecuteAsync(
        AgentRunCommand command,
        ExecutionCommandContext context)
    {
        var settingCommand = (SettingCommand)command;
        if (!IsJsonObject(settingCommand.SettingContent))
        {
            await context.SendErrorAsync("SettingContent must be a JSON object string.");
            return default;
        }

        var normalizedSettings = await context.ExecutionCoordinator.NormalizeSettingsAsync(
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

    private static bool IsJsonObject(string settingContent)
    {
        if (string.IsNullOrWhiteSpace(settingContent))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(settingContent);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
