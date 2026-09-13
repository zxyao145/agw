using Agw.Shared.Runtime;

namespace Agw.Files.Abstracts;

/// <summary>
/// 提供 project 文件系统所需的项目元数据。 实现是在 Project 模块中
/// </summary>
public interface IProjectFileSystemConfigurationProvider
{
    Task<ProjectFileSystemConfiguration?> GetAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>Reauthorizes a captured workspace without loading current directory configuration.</summary>
    async Task<string?> GetOwnerUserIdAsync(Guid projectId, CancellationToken cancellationToken) =>
        (await GetAsync(projectId, cancellationToken).ConfigureAwait(false))?.OwnerUserId;
}

public sealed record ProjectFileSystemConfiguration(
    string Name,
    string? Workspace,
    string? OwnerUserId = null,
    IReadOnlyList<ProjectWorkspaceDirectory>? AdditionalDirectories = null
);
