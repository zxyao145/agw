using System.Net.WebSockets;

using Agw.Agents.Application;
using Agw.Api.Contracts;
using Agw.Shared.Contracts.Tasks;

namespace Agw.Api.Execution;

internal sealed class ExecCommandStrategy : IExecutionCommandStrategy
{
    private const string BusyMessage = "当前任务未执行完毕，请稍候再执行。";

    public bool CanHandle(AgentRunCommand command) => command is ExecCommand;

    public async Task<ExecutionCommandResult> ExecuteAsync(
        AgentRunCommand command,
        ExecutionCommandContext context)
    {
        var execCommand = (ExecCommand)command;
        if (context.ConnectionState.HasRunningExecution)
        {
            await context.SendErrorAsync(BusyMessage);
            return default;
        }

        var settings = context.ConnectionState.CurrentSettings ?? CreateDefaultSettings();
        if (context.ConnectionState.CurrentSettings == null)
        {
            context.ConnectionState.ApplySettings(settings);
        }

        if (context.ConnectionState.ShouldRefreshSessionImmediately)
        {
            context.ConnectionState.ClearSession();
            context.AgentSession = await DisposeSessionAsync(context.AgentSession);
        }

        var taskResolution = await context.ExecutionCoordinator.ResolveTaskAsync(
            new ExecutionTaskRequest(
                ExecutionId: context.AgentId,
                AgentType: execCommand.AgentType,
                TaskId: settings.TaskId,
                ProjectId: settings.ProjectId,
                Input: ExecutionInputTextExtractor.ExtractAgentflowInputText(execCommand.Input),
                Resume: settings.Resume,
                User: context.CurrentUser),
            context.CancellationToken);
        var task = taskResolution.Task;
        var contextError = taskResolution.Error;
        if (contextError != null)
        {
            await context.CloseConnectionAsync(
                WebSocketCloseStatus.InvalidPayloadData,
                context.ExtractReason(contextError) ?? "Invalid request payload");
            return new ExecutionCommandResult(CloseConnection: true);
        }

        var executionStartResult = await context.ExecutionCoordinator.StartStreamingExecutionAsync(
            new StreamingExecutionStartRequest(
                AgentId: context.AgentId,
                Task: task!,
                Command: execCommand,
                CurrentSession: context.AgentSession,
                Settings: settings,
                WebSocket: context.WebSocket,
                SendLock: context.SendLock),
            context.CancellationToken);
        var updatedSession = executionStartResult.AgentSession;
        var activeExecution = executionStartResult.ActiveExecution;
        context.AgentSession = updatedSession;

        if (activeExecution == null)
        {
            return default;
        }

        if (!context.ConnectionState.TryStartExecution(activeExecution))
        {
            await activeExecution.DisposeAsync();
            await context.SendErrorAsync(BusyMessage);
            return default;
        }

        if (execCommand.AgentType == AgentRuntimeType.Agent && context.AgentSession != null)
        {
            context.ConnectionState.MarkSessionReady(settings);
        }

        context.ObserveExecution(activeExecution.ExecutionTask);
        return default;
    }

    private static SettingCommand CreateDefaultSettings()
    {
        return new SettingCommand(
            projectId: ProjectDefaults.DefaultBuiltInId,
            taskId: Guid.NewGuid(),
            null
            );
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
