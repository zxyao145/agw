using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agentflows.Runtime;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Runtimes;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Exceptions;
using static Agw.Agents.Execution.Inbound.Facades.AgentExecutionMapping;
using AgentExecuteByIdRequest = Agw.Agents.Execution.Agents.Contracts.AgentExecuteByIdRequest;

namespace Agw.Agents.Execution.Inbound.Facades;

public sealed class InProcessAgentExecutionRunner : IAgentExecutionRunner
{
    private readonly IAgentRuntimeService _agentRuntimeService;
    private readonly AgentTurnExecutor _turnExecutor;
    private readonly IAgentflowRuntimeService _agentflowRuntimeService;
    private readonly IConversationExecutionGate? _gate;

    public InProcessAgentExecutionRunner(
        IAgentRuntimeService agentRuntimeService,
        AgentTurnExecutor turnExecutor,
        IAgentflowRuntimeService agentflowRuntimeService,
        IConversationExecutionGate? gate = null
    )
    {
        _agentRuntimeService = agentRuntimeService;
        _turnExecutor = turnExecutor;
        _agentflowRuntimeService = agentflowRuntimeService;
        _gate = gate;
    }

    public async IAsyncEnumerable<AgentExecutionEvent> ExecuteStreamingAsync(
        AgentExecutionRequest request,
        ResolvedAgentTarget target,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await using var executionLease = await AcquireInProcessLeaseAsync(request, cancellationToken);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            executionLease?.HandleLostToken ?? CancellationToken.None
        );
        cancellationToken = executionCancellation.Token;
        using var sessionContext = ConversationSessionContext.Push(
            request.Task.ProjectId,
            request.Task.ContextId,
            request.Task.Generation
        );

        if (target.Kind == AgentTargetKind.Agent)
        {
            var task = ProjectTaskProjectionMapper.Map(request.Task);
            var settings = new ExecutionSettings(
                task.ProjectId,
                contextId: task.ContextId,
                permissionMode: MapPermissionMode(request.PermissionMode),
                resume: request.Resume
            );
            await using var runtime = await _agentRuntimeService
                .CreateRuntimeAsync(target.Id, task, settings, cancellationToken)
                .ConfigureAwait(false);
            if (runtime == null)
            {
                throw new AgwException(ErrorCodes.UnableToCreateAgentSession);
            }

            await foreach (
                var message in _turnExecutor
                    .ExecuteStreamingAsync(
                        runtime,
                        request.Input,
                        new UnattendedInteractionHandler(MapPermissionMode(request.PermissionMode)),
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            )
            {
                EnsureHumanInteractionAllowed(request, message);
                yield return new AgentExecutionEvent(null, message);
            }
            yield break;
        }

        await foreach (
            var message in _agentflowRuntimeService
                .ExecuteStreamingAsync(
                    target.Id,
                    ExtractText(request.Input),
                    cancellationToken,
                    request.Task.ProjectId,
                    request.Task.ContextId,
                    request.ExecutionId,
                    interactionHandler: new UnattendedInteractionHandler(MapPermissionMode(request.PermissionMode)),
                    permissionMode: MapPermissionMode(request.PermissionMode)
                )
                .ConfigureAwait(false)
        )
        {
            EnsureHumanInteractionAllowed(request, message);
            yield return new AgentExecutionEvent(null, message);
        }
    }

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        ResolvedAgentTarget target,
        CancellationToken cancellationToken
    )
    {
        await using var executionLease = await AcquireInProcessLeaseAsync(request, cancellationToken);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            executionLease?.HandleLostToken ?? CancellationToken.None
        );
        cancellationToken = executionCancellation.Token;
        using var sessionContext = ConversationSessionContext.Push(
            request.Task.ProjectId,
            request.Task.ContextId,
            request.Task.Generation
        );

        IReadOnlyList<AgwMessage> messages;
        if (target.Kind == AgentTargetKind.Agent)
        {
            var result = await _agentRuntimeService
                .ExecuteByIdAsync(
                    new AgentExecuteByIdRequest(
                        [AgwMessageUtil.CreateUserChatMessage(request.Input)],
                        target.Id,
                        request.ExecutionId,
                        request.Task.ProjectId,
                        request.Task.ContextId
                    )
                    {
                        PermissionMode = MapPermissionMode(request.PermissionMode),
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (result == null)
            {
                throw new AgwException(ErrorCodes.AgentNotFound);
            }
            messages = result.Messages;
        }
        else
        {
            var result = await _agentflowRuntimeService
                .ExecuteAsync(
                    target.Id,
                    request.ExecutionId,
                    [AgwMessageUtil.CreateUserChatMessage(request.Input)],
                    cancellationToken,
                    request.Task.ProjectId,
                    request.Task.ContextId,
                    MapPermissionMode(request.PermissionMode)
                )
                .ConfigureAwait(false);
            if (result == null)
            {
                throw new AgwException(ErrorCodes.ResourceNotFound, "The Agentflow was not found.");
            }
            messages = result.Messages;
        }

        foreach (var message in messages)
        {
            EnsureHumanInteractionAllowed(request, message);
        }
        return new AgentExecutionResult(request.ExecutionId, AgentExecutionState.Completed, messages);
    }

    private async Task<Agw.Shared.Contracts.Coordination.IApplicationLockLease?> AcquireInProcessLeaseAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken
    )
    {
        var gate = _gate;
        return gate == null
            ? null
            : await gate.AcquireAsync(request.Task.ProjectConversationId, request.Task.Generation, cancellationToken);
    }
}
