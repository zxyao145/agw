namespace Agw.Files.Abstracts;

/// <summary>
/// 解析 project 配置的 workspace。实现方式可能会缓存已解析的文件系统，并负责释放其资源。
/// </summary>
public interface IAgwFileSystemResolver
{
    /// <summary>
    /// Resolves the file system associated with the specified project.
    /// </summary>
    /// <param name="projectId">The project whose file system should be resolved.</param>
    /// <param name="ct">A token used to cancel the resolution operation.</param>
    /// <returns>The file system configured for the project.</returns>
    Task<IAgwFileSystem> ResolveAsync(Guid projectId, CancellationToken ct);
}
