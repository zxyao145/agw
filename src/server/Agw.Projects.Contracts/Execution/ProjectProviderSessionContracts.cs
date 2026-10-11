namespace Agw.Projects.Contracts.Execution;

public sealed record ProjectProviderSessionReference(
    Guid ProjectId,
    string ContextId,
    Guid AgentId,
    string ExternalAgentName,
    int Generation = 0
);

public interface IProjectProviderSessionFacade
{
    /// <summary>
    /// 读取绑定组当前生效的 provider session ID；没有生效记录时返回 null。
    /// Reads the binding group's active provider session ID; returns null when there is no active record.
    /// </summary>
    Task<string?> GetProviderSessionIdAsync(
        ProjectProviderSessionReference reference,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// 把 provider session ID 保存为绑定组的生效记录，返回保存后的规范化 ID。
    /// Saves the provider session ID as the binding group's active record and returns the saved normalized ID.
    /// </summary>
    Task<string> SaveProviderSessionIdAsync(
        ProjectProviderSessionReference reference,
        string providerSessionId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    );
}
