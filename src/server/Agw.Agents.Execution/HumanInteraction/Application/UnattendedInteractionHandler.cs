namespace Agw.Agents.Execution.HumanInteraction.Application;

/// <summary>
/// 无人值守执行的决定来源：需要用户决定的请求使执行失败；自动决定已在 Agent 管线内完成。
/// The decision source of unattended execution: requests that need a user fail the execution; automatic decisions already happened in the Agent pipeline.
/// </summary>
internal sealed class UnattendedInteractionHandler : IInteractionHandler
{
    public ValueTask<InteractionResolution> ResolveAsync(
        InteractionRequest request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        InteractionRules.RequireInteractiveExecution(request, HumanInteractionPolicy.Reject);
        return ValueTask.FromResult<InteractionResolution>(new InteractionResolution.Pending(request));
    }
}
