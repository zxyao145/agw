using System.Net.WebSockets;

using Agw.Agents.Contracts;
using Agw.Shared.Contracts.Tasks;

namespace Agw.Agents.Application.Execution.CommandStrategies;

internal sealed class ExecCommandStrategy : IExecutionCommandStrategy
{
    private readonly ExecutionRuntimeStarter _runtimeStarter;

    private const string BusyMessage = "The previous session is currently in progress, please wait and execute again.";


    public ExecCommandStrategy(ExecutionRuntimeStarter runtimeStarter)
    {
        _runtimeStarter = runtimeStarter;
    }


    public bool CanHandle(AgentRunCommand command) => command is ExecCommand;

    public async Task<ExecutionCommandResult> ExecuteAsync(
        AgentRunCommand command,
        ExecutionCommandContext context)
    {
        var execCommand = (ExecCommand)command;
        if (context.RuntimeSession is { HasActiveTurn: true })
        {
            // Only one turn can stream on a connection at a time.
            await context.SendErrorAsync(BusyMessage);
            return default;
        }

        // Reuse the latest client settings when available; otherwise initialize a default execution context.
        var settings = context.ConnectionState.CurrentSettings ?? CreateDefaultSettings();
        if (context.ConnectionState.CurrentSettings == null)
        {
            context.ConnectionState.ApplySettings(settings);
        }

        // If settings changed while a session was idle, dispose the stale session before starting again.
        if (context.ConnectionState.ShouldRefreshSessionImmediately)
        {
            context.ConnectionState.ClearSession();
            context.RuntimeSession = await ExecutionRuntimeStarter.DisposeSessionAsync(context.RuntimeSession);
        }

        if (!context.ConnectionState.TryGetResolvedTask(settings, out var task))
        {
            // Keep task resolution in ExecCommandStrategy rather than SettingCommandStrategy because resolving can
            // create/validate execution state. A SettingCommand should only configure the socket; side effects belong
            // to the command that actually starts a run.
            // Resolve the task once per unchanged SettingCommand, creating it when the client is starting fresh.
            var taskResolution = await _runtimeStarter.ResolveTaskAsync(
                new ExecutionTaskRequest(
                    TaskId: null,
                    ProjectId: settings.ProjectId,
                    ContextId: settings.ContextId,
                    Input: AgwUserInputUtil.ExtractInputText(execCommand.Input),
                    Resume: settings.Resume,
                    User: context.CurrentUser),
                context.CancellationToken);
            var contextError = taskResolution.Error;
            if (contextError != null)
            {
                await context.CloseConnectionAsync(
                    WebSocketCloseStatus.InvalidPayloadData,
                    context.ExtractReason(contextError) ?? "Invalid request payload");
                return new ExecutionCommandResult(CloseConnection: true);
            }

            task = taskResolution.Task!;
            context.ConnectionState.MarkTaskResolved(settings, task);
        }

        // Start the runtime and capture both the session and the active turn that will stream output.
        var executionStartResult = await _runtimeStarter.StartAsync(
            new StreamingExecutionStartRequest(
                AgentId: context.AgentId,
                Task: task!,
                Command: execCommand,
                CurrentSession: context.RuntimeSession,
                Settings: settings,
                MessageSink: new WebSocketExecutionMessageSink(context.WebSocket, context.SendLock)),
            context.CancellationToken);
        var updatedSession = executionStartResult.RuntimeSession;
        var activeTurn = executionStartResult.ActiveTurn;
        context.RuntimeSession = updatedSession;

        if (activeTurn == null)
        {
            return default;
        }

        if (context.RuntimeSession != null)
        {
            // For agent executions, mark the session snapshot that can be reused on the next request.
            context.ConnectionState.MarkSessionReady(settings);
        }

        // Fire-and-forget observation keeps the socket loop responsive while the turn streams in the background.
        context.ObserveTurn(activeTurn.ExecutionTask);
        return default;
    }

    private static SettingCommand CreateDefaultSettings()
    {
        return new SettingCommand(
            projectId: ProjectDefaults.DefaultBuiltInId,
            contextId: null);
    }

}
