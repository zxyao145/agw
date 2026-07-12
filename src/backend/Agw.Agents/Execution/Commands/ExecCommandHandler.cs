using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Contracts;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;

namespace Agw.Agents.Execution.Commands;

public sealed class ExecCommandHandler : ExecutionCommandHandler<ExecCommand>
{
    private readonly IRuntimeFactory _runtimeFactory;
    private readonly IProjectAppService _projectAppService;

    public ExecCommandHandler(
        IRuntimeFactory runtimeFactory,
        IProjectAppService projectAppService)
    {
        _runtimeFactory = runtimeFactory;
        _projectAppService = projectAppService;
    }

    protected override async Task HandleAsync(
        ExecCommand command,
        ExecutionConnection connection,
        CancellationToken cancellationToken)
    {
        if (!command.AgentId.HasValue || command.AgentId.Value == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.agentId is required.");
        }

        if (connection.Runtime is { HasActiveTurn: true })
        {
            await connection.SendErrorAsync(ExecutionConnection.BusyMessage);
            return;
        }

        connection.Settings ??= new SettingCommand(ProjectDefaults.DefaultBuiltInId, contextId: null);
        if (connection.ResolvedTask == null)
        {
            var resolution = await _runtimeFactory.ResolveTaskAsync(
                new ExecutionTaskRequest(
                    TaskId: null,
                    ProjectId: connection.Settings.ProjectId,
                    ContextId: connection.Settings.ContextId,
                    Input: AgwMessageUtil.ExtractInputText(command.Input),
                    Resume: connection.Settings.Resume,
                    User: connection.UserName),
                cancellationToken);
            connection.ResolvedTask = resolution.Task
                ?? throw new AgwException(ErrorCodes.InvalidParam, "Execution task could not be resolved.");
        }

        var target = new ExecutionTarget(command.AgentId.Value, command.AgentType);
        if (connection.Target != null && connection.Target != target)
        {
            await connection.ReplaceRuntimeAsync();
        }

        var project = await _projectAppService.GetAsync(connection.ResolvedTask.ProjectId)
            ?? throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Project '{connection.ResolvedTask.ProjectId}' was not found.");
        var workspace = Path.GetFullPath(
            PathUtil.ExpandTilde(project.GetMustWorkspace().Trim()));
        var turnContext = new RuntimeTurnContext(
            connection.Settings,
            connection.UserName,
            workspace,
            connection.MessageSink,
            pending => connection.SetWaitingForHuman(pending != null));
        var start = await _runtimeFactory.StartAsync(
            new RuntimeStartRequest(
                target.AgentId,
                connection.ResolvedTask,
                command,
                connection.Runtime,
                turnContext),
            connection.HostToken);
        connection.Runtime = start.Runtime;
        connection.Target = target;
        if (start.ActiveTurn == null)
        {
            await connection.SendErrorAsync("Agent execution could not be started.");
        }
    }
}
