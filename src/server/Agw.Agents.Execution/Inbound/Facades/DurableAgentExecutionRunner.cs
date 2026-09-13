using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using static Agw.Agents.Execution.Inbound.Facades.AgentExecutionMapping;

namespace Agw.Agents.Execution.Inbound.Facades;

public sealed class DurableAgentExecutionRunner : IAgentExecutionRunner
{
    private readonly IDurableExecutionClient DurableClient;

    public DurableAgentExecutionRunner(IDurableExecutionClient durableClient)
    {
        DurableClient = durableClient;
    }

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        ResolvedAgentTarget target,
        CancellationToken cancellationToken
    )
    {
        await StartDurableAsync(request, target, cancellationToken).ConfigureAwait(false);
        var outcome = await DurableClient
            .WaitForActionableOutcomeAsync(request.ExecutionId, request.OwnerUserId, cancellationToken)
            .ConfigureAwait(false);
        if (
            outcome.Status == DurableExecutionStatus.WaitingForHuman
            && request.HumanInteractionPolicy == HumanInteractionPolicy.Reject
        )
        {
            await DurableClient
                .InterruptAsync(
                    request.ExecutionId,
                    request.OwnerUserId,
                    "This unattended execution does not support human interaction.",
                    cancellationToken
                )
                .ConfigureAwait(false);
            throw new AgwException(ErrorCodes.AgentExecutionFailed, "Human interaction is not supported.");
        }

        var result = Map(outcome);
        if (result.State is AgentExecutionState.Failed or AgentExecutionState.Interrupted)
        {
            throw new AgwException(ErrorCodes.AgentExecutionFailed, result.ErrorMessage ?? "Agent execution failed.");
        }
        return result;
    }

    public async IAsyncEnumerable<AgentExecutionEvent> ExecuteStreamingAsync(
        AgentExecutionRequest request,
        ResolvedAgentTarget target,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await StartDurableAsync(request, target, cancellationToken).ConfigureAwait(false);
        await foreach (
            var executionEvent in DurableClient
                .ReadAsync(request.ExecutionId, request.OwnerUserId, afterCursor: null, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            if (
                request.HumanInteractionPolicy == HumanInteractionPolicy.Reject
                && IsHumanInteraction(executionEvent.Message)
            )
            {
                await DurableClient
                    .InterruptAsync(
                        request.ExecutionId,
                        request.OwnerUserId,
                        "This unattended execution does not support human interaction.",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                throw new AgwException(ErrorCodes.AgentExecutionFailed, "Human interaction is not supported.");
            }
            yield return new AgentExecutionEvent(executionEvent.Cursor, executionEvent.Message);
        }

        var outcome = await DurableClient
            .GetOutcomeAsync(request.ExecutionId, request.OwnerUserId, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Status == DurableExecutionStatus.Failed)
        {
            throw new AgwException(ErrorCodes.AgentExecutionFailed, outcome.ErrorMessage ?? "Agent execution failed.");
        }
    }

    private Task StartDurableAsync(
        AgentExecutionRequest request,
        ResolvedAgentTarget target,
        CancellationToken cancellationToken
    )
    {
        var settings = new ExecutionSettings(
            request.Task.ProjectId,
            request.Task.ContextId,
            permissionMode: MapPermissionMode(request.PermissionMode),
            resume: request.Resume
        );
        settings = settings.WithHumanInteractionPolicy(request.HumanInteractionPolicy);
        return DurableClient.StartAsync(
            new DurableExecutionRequest(
                request.ExecutionId,
                request.OwnerUserId,
                target.Id,
                target.Kind == AgentTargetKind.Agent ? AgentRuntimeType.Agent : AgentRuntimeType.Agentflow,
                request.Input,
                ProjectTaskProjectionMapper.Map(request.Task),
                settings
            ),
            cancellationToken
        );
    }
}
