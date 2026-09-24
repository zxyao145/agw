using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction.Application;

namespace Agw.Agents.Execution.HumanInteraction;

/// <summary>
/// 把当前执行作用域投影为工具需要的交互接口：回答通道、请求登记服务与交互来源。
/// Projects the current execution scope onto the interaction surface tools need: answer channel, request registry and interaction source.
/// </summary>
public sealed class HumanInteractionContextAccessor : IHumanInteractionContextAccessor
{
    private readonly IAgentExecutionContextAccessor _executionContext;

    public HumanInteractionContextAccessor(IAgentExecutionContextAccessor executionContext)
    {
        _executionContext = executionContext;
    }

    public IHumanInteractionChannel? Current => ExecutionScope.Current?.InteractionChannel;

    public IInteractionRequestRegistry? Requests => ExecutionScope.Current?.InteractionRequests;

    public InteractionSource Source =>
        _executionContext.Current?.Node is { } node
            ? new InteractionSource
            {
                NodeId = node.NodeId,
                NodeName = node.NodeName,
                ProviderScopeId = node.ProviderScopeId,
            }
            : new InteractionSource { NodeId = InteractionIdentity.StandaloneNodeId };

    internal InteractionPermissionState? PermissionState => ExecutionScope.Current?.Permissions;
}
