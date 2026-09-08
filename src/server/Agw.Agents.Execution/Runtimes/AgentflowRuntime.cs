using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Turns;

namespace Agw.Agents.Execution.Runtimes;

public sealed class AgentflowRuntime : RuntimeBase
{
    private readonly Guid _agentflowId;
    private readonly AgentExecutionTask _task;
    private readonly SettingCommand _settings;
    private readonly AgentflowRuntimeService _runtimeService;
    private readonly AgentflowCheckpointRuntimeState _checkpointState = new();

    internal AgentflowRuntime(
        Guid agentflowId,
        AgentExecutionTask task,
        SettingCommand settings,
        AgentflowRuntimeService runtimeService
    )
    {
        _agentflowId = agentflowId;
        _task = task;
        _settings = settings;
        _runtimeService = runtimeService;
    }

    internal IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        ExecCommand command,
        IHumanGateApprovalHandler humanGateApprovalHandler,
        CancellationToken cancellationToken
    ) =>
        ExecuteStreamingAsync(
            command,
            humanGateApprovalHandler,
            new PermissionModeState(_settings.PermissionMode),
            cancellationToken
        );

    internal IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        ExecCommand command,
        IHumanGateApprovalHandler humanGateApprovalHandler,
        PermissionModeState permissionState,
        CancellationToken cancellationToken
    ) =>
        _runtimeService.ExecuteStreamingWithPermissionStateAsync(
            _agentflowId,
            command.Input,
            cancellationToken,
            ProjectDefaults.GetDefaultProjectIdentifier(_settings.ProjectId),
            _task.ContextId,
            _task.TaskId,
            humanGateApprovalHandler,
            _settings.EnvironmentVariables,
            _task.ProjectConversationId,
            permissionState,
            command.ExecutionId,
            _checkpointState,
            command.ResumeCheckpoint
        );

    internal IReadOnlySet<Guid> CheckpointOccurrenceIds => _checkpointState.OccurrenceIds;

    internal bool TryGetCheckpoint(Guid occurrenceId, out AgentflowCheckpointSnapshot? checkpoint) =>
        _checkpointState.TryGet(occurrenceId, out checkpoint);

    internal void RemoveCheckpointsAfter(long boundarySequence) => _checkpointState.RemoveAfter(boundarySequence);

    internal void SetPermissionMode(PermissionMode permissionMode)
    {
        _settings.PermissionMode = permissionMode;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _checkpointState.Clear();
    }
}
