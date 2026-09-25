namespace Agw.Agents.Execution.HumanInteraction.Application;

/// <summary>
/// 把需要用户决定的请求交给待处理集合：交互不可用时就地拒绝，无人值守执行直接失败。两种执行模式共用。
/// Hands requests that need a user to the pending-interaction set: declines in place when interaction is unavailable and fails unattended execution. Shared by both execution modes.
/// </summary>
internal sealed class PendingInteractionHandler : IInteractionHandler
{
    private readonly HumanInteractionPolicy _policy;
    private readonly bool _allowInteraction;

    public PendingInteractionHandler(
        HumanInteractionPolicy policy,
        InteractionRequestRegistry requests,
        bool allowInteraction = true
    )
    {
        _policy = policy;
        Registry = requests;
        _allowInteraction = allowInteraction;
    }

    public InteractionRequestRegistry Registry { get; }

    public IInteractionRequestRegistry Requests => Registry;

    public ValueTask<InteractionResolution> ResolveAsync(
        InteractionRequest request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 交互不可用时就地拒绝，执行不进入等待。
        // Decline in place when interaction is unavailable so the execution never waits.
        if (!_allowInteraction)
            return ValueTask.FromResult<InteractionResolution>(
                new InteractionResolution.Resolved(InteractionRules.Decline(request))
            );
        InteractionRules.RequireInteractiveExecution(request, _policy);
        return ValueTask.FromResult<InteractionResolution>(new InteractionResolution.Pending(request));
    }
}
