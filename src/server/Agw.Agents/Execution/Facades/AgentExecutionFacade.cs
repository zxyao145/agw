using System.Runtime.CompilerServices;
using System.Security.Claims;
using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Durable;
using Agw.Agents.Execution.Mapping;
using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AgentExecuteByIdRequest = Agw.Agents.Execution.Agents.Dtos.AgentExecuteByIdRequest;

namespace Agw.Agents.Execution.Facades;

public sealed class AgentExecutionFacade : IAgentExecutionFacade, IDurableAgentExecutionFacade
{
    private readonly IAgentRuntimeService _agentRuntimeService;
    private readonly IAgentflowRuntimeService _agentflowRuntimeService;
    private readonly IAgentCatalogFacade _catalog;
    private readonly IServiceProvider _services;
    private readonly ExecutionProvider _provider;

    public AgentExecutionFacade(
        IAgentRuntimeService agentRuntimeService,
        IAgentflowRuntimeService agentflowRuntimeService,
        IAgentCatalogFacade catalog,
        IServiceProvider services,
        IOptions<ExecutionRuntimeOptions> executionOptions
    )
    {
        _agentRuntimeService = agentRuntimeService;
        _agentflowRuntimeService = agentflowRuntimeService;
        _catalog = catalog;
        _services = services;
        _provider = executionOptions.Value.Provider;
    }

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        using var userScope = PushExecutionUser(request.OwnerUserId);
        var target = await ResolveTargetAsync(request.Target, cancellationToken).ConfigureAwait(false);
        return _provider == ExecutionProvider.Distributed
            ? await ExecuteDurableAsync(request, target, cancellationToken).ConfigureAwait(false)
            : await ExecuteInProcessAsync(request, target, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<AgentExecutionEvent> ExecuteStreamingAsync(
        AgentExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        using var userScope = PushExecutionUser(request.OwnerUserId);
        var target = await ResolveTargetAsync(request.Target, cancellationToken).ConfigureAwait(false);
        if (_provider == ExecutionProvider.Distributed)
        {
            await foreach (
                var executionEvent in ExecuteDurableStreamingAsync(request, target, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                yield return executionEvent;
            }
            yield break;
        }

        if (target.Kind == AgentTargetKind.Agent)
        {
            var task = ProjectTaskProjectionMapper.Map(request.Task);
            var settings = new SettingCommand(task.ProjectId, contextId: task.ContextId) { Resume = request.Resume };
            await using var runtime = await _agentRuntimeService
                .CreateRuntimeAsync(target.Id, task, settings, cancellationToken)
                .ConfigureAwait(false);
            if (runtime == null)
            {
                throw new AgwException(ErrorCodes.UnableToCreateAgentSession);
            }

            await foreach (
                var message in _agentRuntimeService
                    .ExecuteStreamingAsync(runtime, request.Input, cancellationToken)
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
                    request.ExecutionId
                )
                .ConfigureAwait(false)
        )
        {
            EnsureHumanInteractionAllowed(request, message);
            yield return new AgentExecutionEvent(null, message);
        }
    }

    public async Task<AgentExecutionResult> GetOutcomeAsync(
        Guid executionId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    )
    {
        using var userScope = PushExecutionUser(ownerUserId);
        var outcome = await DurableClient
            .GetOutcomeAsync(executionId, ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        return Map(outcome);
    }

    public async IAsyncEnumerable<AgentExecutionEvent> SubscribeAsync(
        Guid executionId,
        string ownerUserId,
        string? afterCursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        using var userScope = PushExecutionUser(ownerUserId);
        await foreach (
            var executionEvent in DurableClient
                .ReadAsync(executionId, ownerUserId, afterCursor, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return new AgentExecutionEvent(executionEvent.Cursor, executionEvent.Message);
        }
    }

    public Task<bool> InterruptAsync(
        Guid executionId,
        string ownerUserId,
        string reason,
        CancellationToken cancellationToken = default
    ) => InterruptCoreAsync(executionId, ownerUserId, reason, cancellationToken);

    private async Task<bool> InterruptCoreAsync(
        Guid executionId,
        string ownerUserId,
        string reason,
        CancellationToken cancellationToken
    )
    {
        using var userScope = PushExecutionUser(ownerUserId);
        return await DurableClient
            .InterruptAsync(executionId, ownerUserId, reason, cancellationToken)
            .ConfigureAwait(false);
    }

    private IDurableExecutionClient DurableClient =>
        _services.GetService<IDurableExecutionClient>()
        ?? throw new AgwException(ErrorCodes.DurableExecutionUnavailable);

    private async Task<AgentExecutionResult> ExecuteInProcessAsync(
        AgentExecutionRequest request,
        ResolvedTarget target,
        CancellationToken cancellationToken
    )
    {
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
                    ),
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
                    request.Task.ContextId
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

    private async Task<AgentExecutionResult> ExecuteDurableAsync(
        AgentExecutionRequest request,
        ResolvedTarget target,
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

    private async IAsyncEnumerable<AgentExecutionEvent> ExecuteDurableStreamingAsync(
        AgentExecutionRequest request,
        ResolvedTarget target,
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
        ResolvedTarget target,
        CancellationToken cancellationToken
    )
    {
        var settings = ExecutionSettings.FromCommand(
            new SettingCommand(request.Task.ProjectId, contextId: request.Task.ContextId) { Resume = request.Resume }
        );
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

    private async Task<ResolvedTarget> ResolveTargetAsync(AgentTarget target, CancellationToken cancellationToken)
    {
        if (target.Id is { } id && id != Guid.Empty)
        {
            var runtimeType =
                target.Kind == AgentTargetKind.Agent ? AgentRuntimeType.Agent : AgentRuntimeType.Agentflow;
            if (!await _catalog.IsOwnedTargetAsync(runtimeType, id, UserInfoUtil.RequiredUserId, cancellationToken))
            {
                throw new AgwException(ErrorCodes.ResourceNotFound);
            }

            return new ResolvedTarget(target.Kind, id);
        }
        if (target.Kind != AgentTargetKind.Agent || string.IsNullOrWhiteSpace(target.Name))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The Agent execution target is invalid.");
        }

        var descriptor = await _catalog
            .FindDiscoverableByNameAsync(target.Name, cancellationToken)
            .ConfigureAwait(false);
        return descriptor == null
            ? throw new AgwException(ErrorCodes.AgentNotFound, $"Agent '{target.Name}' was not found.")
            : new ResolvedTarget(AgentTargetKind.Agent, descriptor.Id);
    }

    private static AgentExecutionResult Map(DurableExecutionOutcome outcome) =>
        new(outcome.ExecutionId, Map(outcome.Status), [], outcome.ErrorMessage);

    private static AgentExecutionState Map(DurableExecutionStatus status) =>
        status switch
        {
            DurableExecutionStatus.Queued => AgentExecutionState.Queued,
            DurableExecutionStatus.Running or DurableExecutionStatus.Resuming => AgentExecutionState.Running,
            DurableExecutionStatus.WaitingForHuman => AgentExecutionState.WaitingForHuman,
            DurableExecutionStatus.Completed => AgentExecutionState.Completed,
            DurableExecutionStatus.Failed => AgentExecutionState.Failed,
            DurableExecutionStatus.Interrupted => AgentExecutionState.Interrupted,
            _ => throw new AgwException(ErrorCodes.InvalidParam, $"Unsupported execution status '{status}'."),
        };

    private static void EnsureHumanInteractionAllowed(AgentExecutionRequest request, AgwMessage message)
    {
        if (request.HumanInteractionPolicy == HumanInteractionPolicy.Reject && IsHumanInteraction(message))
        {
            throw new AgwException(ErrorCodes.AgentExecutionFailed, "Human interaction is not supported.");
        }
    }

    private static bool IsHumanInteraction(AgwMessage message) =>
        AgentExecutionMessageProtocol.GetMessageType(message)
            is "human-interaction-request"
                or "tool-approval-request"
                or "human-gate-request";

    private static string ExtractText(AgwUserInput input) =>
        string.Join(
            "\n",
            input.Contents.OfType<AgwTextContent>().Select(content => content.Content).Where(value => value != null)
        );

    private static ClaimsPrincipal CreateUserPrincipal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "AgentExecutionFacade"));

    private static IDisposable PushExecutionUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new AgwException(ErrorCodes.AuthenticationRequired);
        }

        var normalizedUserId = userId.Trim();
        if (
            UserInfoUtil.IsContextActive
            && !string.Equals(UserInfoUtil.RequiredUserId, normalizedUserId, StringComparison.Ordinal)
        )
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        return UserInfoUtil.Push(CreateUserPrincipal(normalizedUserId));
    }

    private sealed record ResolvedTarget(AgentTargetKind Kind, Guid Id);
}
