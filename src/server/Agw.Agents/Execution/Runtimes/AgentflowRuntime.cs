using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Contracts;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;

namespace Agw.Agents.Execution.Runtimes;

public sealed class AgentflowRuntime : RuntimeBase
{
    private readonly Guid _agentflowId;
    private readonly TaskProjection _task;
    private readonly SettingCommand _settings;
    private readonly AgentflowRuntimeService _runtimeService;

    internal AgentflowRuntime(
        Guid agentflowId,
        TaskProjection task,
        SettingCommand settings,
        AgentflowRuntimeService runtimeService)
    {
        _agentflowId = agentflowId;
        _task = task;
        _settings = settings;
        _runtimeService = runtimeService;
    }

    internal IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        ExecCommand command,
        IHumanGateApprovalHandler humanGateApprovalHandler,
        CancellationToken cancellationToken) =>
        _runtimeService.ExecuteStreamingAsync(
            _agentflowId,
            AgwMessageUtil.ExtractInputText(command.Input),
            cancellationToken,
            ProjectDefaults.GetDefaultProjectIdentifier(_settings.ProjectId),
            _task.ContextId,
            _task.TaskId,
            humanGateApprovalHandler,
            _settings.EnvironmentVariables);
}
