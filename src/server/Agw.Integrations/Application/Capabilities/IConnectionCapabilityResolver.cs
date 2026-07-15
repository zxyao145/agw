namespace Agw.Integrations.Application.Capabilities;

/// <summary>
/// 解析指定 Connection 可向项目中的 Agent 提供的工具、Plugin Skill、Warning 和资源 Lease。
/// </summary>
public interface IConnectionCapabilityResolver
{
    /// <summary>
    /// 按 Connection ID 批量解析当前项目可用的集成能力。
    /// </summary>
    /// <param name="projectId">使用这些能力的项目 ID。</param>
    /// <param name="connectionIds">需要解析的 Connection ID 集合；重复 ID 会被去重。</param>
    /// <param name="cancellationToken">用于取消异步解析操作的 Token。</param>
    /// <returns>
    /// 包含 Native 工具、MCP 工具、Plugin Skill、结构化 Warning 和所拥有资源 Lease 的解析结果。
    /// 调用方负责释放该结果。
    /// </returns>
    Task<ConnectionCapabilityResolution> ResolveAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> connectionIds,
        CancellationToken cancellationToken);
}
