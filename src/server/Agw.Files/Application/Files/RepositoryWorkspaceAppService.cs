using System.Globalization;
using Agw.Files.Abstracts;
using Agw.Shared.Exceptions;

namespace Agw.Files.Application.Files;

public sealed class RepositoryWorkspaceAppService
{
    private readonly IAgwFileSystemResolver _resolver;
    private readonly IRepositoryWorkspace _workspace;
    private readonly TimeProvider _time;

    public RepositoryWorkspaceAppService(
        IAgwFileSystemResolver resolver,
        IRepositoryWorkspace workspace,
        TimeProvider time
    )
    {
        _resolver = resolver;
        _workspace = workspace;
        _time = time;
    }

    public async Task<RepositoryWorkspaceState> ReadAsync(Guid projectId, CancellationToken ct) =>
        await _workspace.ReadAsync(await ResolveRootAsync(projectId, ct), ct);

    public Task<RepositoryWorkspaceState> CloneAsync(
        Guid projectId,
        string url,
        string? name,
        CancellationToken ct,
        string? proxyUrl = null,
        bool intoRoot = false
    ) => RunAsync(projectId, root => _workspace.CloneAsync(root, url, name, ct, proxyUrl, intoRoot), ct);

    /// <summary>
    /// <paramref name="feature"/> 非空时向已有 feature 添加仓库，否则以 <c>yyMMdd-name</c> 新建；<paramref name="date"/>
    /// 缺省为 UTC 日期。Adds repositories to <paramref name="feature"/> when set, otherwise creates <c>yyMMdd-name</c>;
    /// <paramref name="date"/> defaults to the UTC date.
    /// </summary>
    public Task<RepositoryWorkspaceState> CreateFeatureAsync(
        Guid projectId,
        string? name,
        string? feature,
        DateOnly? date,
        IReadOnlyList<string> repositories,
        CancellationToken ct
    )
    {
        var existing = !string.IsNullOrWhiteSpace(feature);
        if (!existing && string.IsNullOrWhiteSpace(name))
            throw new AgwException(ErrorCodes.InvalidParam, "A feature name is required.");
        var day = date ?? DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var branch = existing ? feature! : $"{day.ToString("yyMMdd", CultureInfo.InvariantCulture)}-{name!.Trim()}";
        return RunAsync(projectId, root => _workspace.CreateFeatureAsync(root, branch, repositories, existing, ct), ct);
    }

    public Task<RepositoryWorkspaceState> DeleteFeatureAsync(Guid projectId, string feature, CancellationToken ct) =>
        RunAsync(projectId, root => _workspace.DeleteFeatureAsync(root, feature, ct), ct);

    public Task<RepositoryWorkspaceState> SwitchBranchAsync(
        Guid projectId,
        string feature,
        string repository,
        string branch,
        bool createBranch,
        CancellationToken ct
    ) =>
        RunAsync(
            projectId,
            root => _workspace.SwitchBranchAsync(root, feature, repository, branch.Trim(), createBranch, ct),
            ct
        );

    public Task<RepositoryWorkspaceState> MergeAsync(
        Guid projectId,
        string feature,
        string repository,
        CancellationToken ct
    ) => RunAsync(projectId, root => _workspace.MergeAsync(root, feature, repository, ct), ct);

    public Task<RepositoryWorkspaceState> SyncAsync(
        Guid projectId,
        string repository,
        CancellationToken ct,
        string? proxyUrl = null
    ) => RunAsync(projectId, root => _workspace.SyncAsync(root, repository, ct, proxyUrl), ct);

    public Task<RepositoryWorkspaceState> RestoreDefaultBranchAsync(
        Guid projectId,
        string repository,
        CancellationToken ct
    ) => RunAsync(projectId, root => _workspace.RestoreDefaultBranchAsync(root, repository, ct), ct);

    public async Task<RepositoryTree> ListTreeAsync(
        Guid projectId,
        string repository,
        string branch,
        string? path,
        CancellationToken ct
    ) => await _workspace.ListTreeAsync(await ResolveRootAsync(projectId, ct), repository, branch, path ?? "", ct);

    public async Task<RepositoryChange[]> ListChangesAsync(
        Guid projectId,
        string repository,
        string branch,
        CancellationToken ct
    ) => await _workspace.ListChangesAsync(await ResolveRootAsync(projectId, ct), repository, branch, ct);

    public async Task<RepositoryBlob> ReadBlobAsync(
        Guid projectId,
        string repository,
        string branch,
        string path,
        CancellationToken ct
    ) => await _workspace.ReadBlobAsync(await ResolveRootAsync(projectId, ct), repository, branch, path, ct);

    private async Task<RepositoryWorkspaceState> RunAsync(
        Guid projectId,
        Func<string, Task> operation,
        CancellationToken ct
    )
    {
        var root = await ResolveRootAsync(projectId, ct);
        await operation(root);
        return await _workspace.ReadAsync(root, ct);
    }

    private async Task<string> ResolveRootAsync(Guid projectId, CancellationToken ct)
    {
        var fileSystem = await _resolver.ResolveAsync(projectId, ct);
        if (fileSystem == null)
            throw new AgwException(ErrorCodes.ResourceNotFound);
        if (fileSystem is not ILocalFileSystem local)
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Repository workspaces require a local project file system."
            );
        return local.ResolvePhysicalPath("");
    }
}
