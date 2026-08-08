using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Turns;
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
        ExecuteStreamingAsync(
            command,
            humanGateApprovalHandler,
            new PermissionModeState(_settings.PermissionMode),
            cancellationToken);

    internal IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        ExecCommand command,
        IHumanGateApprovalHandler humanGateApprovalHandler,
        PermissionModeState permissionState,
        CancellationToken cancellationToken) =>
        _runtimeService.ExecuteStreamingWithPermissionStateAsync(
            _agentflowId,
            AgwMessageUtil.ExtractInputText(command.Input),
            cancellationToken,
            ProjectDefaults.GetDefaultProjectIdentifier(_settings.ProjectId),
            _task.ContextId,
            _task.TaskId,
            humanGateApprovalHandler,
            _settings.EnvironmentVariables,
            _task.ProjectConversationId,
            permissionState);

    internal void SetPermissionMode(PermissionMode permissionMode)
    {
        _settings.PermissionMode = permissionMode;
    }
}
