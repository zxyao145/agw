namespace Agw.Files.Abstracts;

public interface IProjectWorkspaceAccess
{
    bool IsolationEnabled { get; }

    Task ValidateAsync(
        IReadOnlyList<string> paths,
        Guid? creatingProjectId = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// 判断路径是否可由当前用户访问。Checks whether the paths are accessible to the current user.
    /// </summary>
    Task<bool> IsAccessibleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}
