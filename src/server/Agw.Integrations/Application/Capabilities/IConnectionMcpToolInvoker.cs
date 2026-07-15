using Microsoft.Extensions.AI;

namespace Agw.Integrations.Application.Capabilities;

/// <summary>
/// 在独立依赖注入 Scope 中调用某个 Connection 对应的 MCP 工具。
/// </summary>
public interface IConnectionMcpToolInvoker
{
    /// <summary>
    /// 使用最新的 Connection、Plugin 定义和凭据调用指定 MCP Operation。
    /// </summary>
    /// <param name="connectionId">提供该 MCP 工具的 Connection ID。</param>
    /// <param name="sourceId">Connector 中声明的 MCP Capability Source ID。</param>
    /// <param name="operationName">MCP Server 暴露的原始 Operation 名称。</param>
    /// <param name="arguments">传递给 MCP Operation 的参数。</param>
    /// <param name="cancellationToken">用于取消调用的 Token。</param>
    /// <returns>MCP Operation 返回的结果。</returns>
    ValueTask<object?> InvokeAsync(
        Guid connectionId,
        string sourceId,
        string operationName,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken);
}

/// <summary>
/// 表示当前依赖注入 Scope 内可执行的 MCP 调用会话。
/// </summary>
internal interface IConnectionMcpInvocationSession
{
    /// <summary>
    /// 在当前会话中重新解析 MCP Source 和凭据，并调用指定 Operation。
    /// </summary>
    /// <param name="connectionId">提供该 MCP 工具的 Connection ID。</param>
    /// <param name="sourceId">Connector 中声明的 MCP Capability Source ID。</param>
    /// <param name="operationName">MCP Server 暴露的原始 Operation 名称。</param>
    /// <param name="arguments">传递给 MCP Operation 的参数。</param>
    /// <param name="cancellationToken">用于取消调用的 Token。</param>
    /// <returns>MCP Operation 返回的结果。</returns>
    ValueTask<object?> InvokeMcpToolAsync(
        Guid connectionId,
        string sourceId,
        string operationName,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken);
}
