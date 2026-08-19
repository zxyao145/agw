using Agw.Integrations.Domain.Plugins;
using Microsoft.Extensions.AI;

namespace Agw.Integrations.Application.Capabilities;

/// <summary>
/// 为某类 Native Capability Source 创建绑定到具体 Connection 的 Agent 工具。
/// </summary>
public interface IConnectionNativeCapabilityProvider
{
    /// <summary>
    /// 获取与 <see cref="NativeCapabilitySourceDefinition.Provider"/> 匹配的 Provider Key。
    /// </summary>
    string Provider { get; }

    /// <summary>
    /// 为指定 Connection 和 Project 创建 Native 工具。
    /// </summary>
    /// <param name="context">包含 Connection ID、Project ID、Alias 和 Source 定义的创建上下文。</param>
    /// <returns>
    /// 绑定到该 Connection 的工具集合。每个工具名称都必须以
    /// <c>{Alias}__</c> 为前缀。
    /// </returns>
    IReadOnlyList<AITool> CreateTools(ConnectionNativeCapabilityContext context);
}

public sealed class ConnectionNativeCapabilityContext
{
    public required Guid ConnectionId { get; init; }

    public required Guid ProjectId { get; init; }

    public required string Alias { get; init; }

    public required NativeCapabilitySourceDefinition Source { get; init; }
}
