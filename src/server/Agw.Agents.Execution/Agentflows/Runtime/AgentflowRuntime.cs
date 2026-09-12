using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Runtimes;

namespace Agw.Agents.Execution.Agentflows.Runtime;

public sealed class AgentflowRuntime : RuntimeBase
{
    private readonly Guid _agentflowId;
    private readonly AgentExecutionTask _task;
    private ExecutionSettings _settings;
    private readonly AgentflowRuntimeService _runtimeService;
    private readonly AgentflowCheckpointRuntimeState _checkpointState = new();

    internal AgentflowRuntime(
        Guid agentflowId,
        AgentExecutionTask task,
        ExecutionSettings settings,
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
        IInteractionHandler interactionHandler,
        CancellationToken cancellationToken
    ) =>
        ExecuteStreamingAsync(
            command,
            interactionHandler,
            new MafPermissionState(_settings.PermissionMode),
            cancellationToken
        );

    internal IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        ExecCommand command,
        IInteractionHandler interactionHandler,
        MafPermissionState permissionState,
        CancellationToken cancellationToken
    ) =>
        _runtimeService.ExecuteStreamingWithPermissionStateAsync(
            _agentflowId,
            command.Input,
            cancellationToken,
            ProjectDefaults.GetDefaultProjectIdentifier(_settings.ProjectId),
            _task.ContextId,
            _task.TaskId,
            interactionHandler,
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

    internal void SetPermissionMode(AgwPermissionMode permissionMode)
    {
        _settings = _settings.WithPermissionMode(permissionMode);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _checkpointState.Clear();
    }
}
