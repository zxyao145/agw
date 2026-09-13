using System.Runtime.CompilerServices;
using System.Security.Claims;
using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Auth.Contracts;
using Agw.Shared.Exceptions;
using static Agw.Agents.Execution.Inbound.Facades.AgentExecutionMapping;

namespace Agw.Agents.Execution.Inbound.Facades;

public sealed class AgentExecutionFacade : IAgentExecutionFacade, IDurableAgentExecutionFacade
{
    private readonly IAgentExecutionRunner _runner;
    private readonly IAgentCatalogFacade _catalog;
    private readonly IDurableExecutionClient? _durableClient;

    public AgentExecutionFacade(
        IAgentExecutionRunner runner,
        IAgentCatalogFacade catalog,
        IDurableExecutionClient? durableClient = null
    )
    {
        _runner = runner;
        _catalog = catalog;
        _durableClient = durableClient;
    }

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        using var userScope = PushExecutionUser(request.OwnerUserId);
        var target = await ResolveTargetAsync(request.Target, cancellationToken);
        return await _runner.ExecuteAsync(request, target, cancellationToken);
    }

    public async IAsyncEnumerable<AgentExecutionEvent> ExecuteStreamingAsync(
        AgentExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        using var userScope = PushExecutionUser(request.OwnerUserId);
        var target = await ResolveTargetAsync(request.Target, cancellationToken);
        await foreach (var item in _runner.ExecuteStreamingAsync(request, target, cancellationToken))
            yield return item;
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
        _durableClient ?? throw new AgwException(ErrorCodes.DurableExecutionUnavailable);

    private async Task<ResolvedAgentTarget> ResolveTargetAsync(AgentTarget target, CancellationToken cancellationToken)
    {
        if (target.Id is { } id && id != Guid.Empty)
        {
            var runtimeType =
                target.Kind == AgentTargetKind.Agent ? AgentRuntimeType.Agent : AgentRuntimeType.Agentflow;
            if (!await _catalog.IsOwnedTargetAsync(runtimeType, id, UserInfoUtil.RequiredUserId, cancellationToken))
            {
                throw new AgwException(ErrorCodes.ResourceNotFound);
            }

            return new ResolvedAgentTarget(target.Kind, id);
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
            : new ResolvedAgentTarget(AgentTargetKind.Agent, descriptor.Id);
    }

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
}
