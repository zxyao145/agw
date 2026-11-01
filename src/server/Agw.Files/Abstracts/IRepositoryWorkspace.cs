namespace Agw.Files.Abstracts;

/// <summary>
/// 项目根目录下的主工作副本（含 <c>.git</c> 目录的子目录）。A root checkout: a project subdirectory with a <c>.git</c> directory.
/// </summary>
public sealed record WorkspaceRepository
{
    public string Name { get; init; } = "";

    /// <summary>远程默认分支；没有 <c>origin/HEAD</c> 时为当前分支。The remote default branch, or the current branch without <c>origin/HEAD</c>.</summary>
    public string DefaultBranch { get; init; } = "";

    /// <summary>当前分支；分离 HEAD 时为空。The current branch; empty when HEAD is detached.</summary>
    public string Branch { get; init; } = "";
    public bool Dirty { get; init; }

    /// <summary>相对上游分支的领先、落后提交数；没有上游时为 null。Commits ahead of and behind the upstream; null without one.</summary>
    public int? Ahead { get; init; }
    public int? Behind { get; init; }
    public string[] Branches { get; init; } = [];

    /// <summary>远程跟踪分支，例如 <c>origin/main</c>。Remote-tracking branches such as <c>origin/main</c>.</summary>
    public string[] RemoteBranches { get; init; } = [];

    /// <summary>读取失败的原因；非空时其他状态字段不可信。Why reading failed; other status fields are unreliable when set.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// <c>.worktrees/&lt;feature&gt;/&lt;repo&gt;</c> 下的一个 worktree。A worktree at <c>.worktrees/&lt;feature&gt;/&lt;repo&gt;</c>.
/// </summary>
public sealed record FeatureRepository
{
    public string Name { get; init; } = "";
    public string Branch { get; init; } = "";
    public bool Dirty { get; init; }

    /// <summary>相对主工作副本默认分支的领先、落后提交数。Commits ahead of and behind the root checkout's default branch.</summary>
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public string? Error { get; init; }
}

public sealed record WorkspaceFeature(string Name, FeatureRepository[] Repositories);

public sealed record RepositoryWorkspaceState(WorkspaceRepository[] Repositories, WorkspaceFeature[] Features);

/// <summary>分支中的一个目录项；路径相对仓库根目录。A directory entry of a branch; the path is relative to the repository.</summary>
public sealed record RepositoryTreeEntry(string Name, string Path, bool IsDirectory);

/// <summary>
/// 分支的一层目录。<see cref="Checkout"/> 为检出该分支的目录（相对 Project 根目录），此时条目来自磁盘；为 null 时条目来自
/// 提交。One directory level of a branch. <see cref="Checkout"/> is the directory where the branch is checked out
/// (relative to the Project root), and the entries then come from the disk; when null they come from the commit.
/// </summary>
public sealed record RepositoryTree(string? Checkout, RepositoryTreeEntry[] Entries);

/// <summary>
/// 未提交的改动；<see cref="Status"/> 为 modified、added、deleted、renamed、untracked 或 conflicted。An uncommitted
/// change; <see cref="Status"/> is modified, added, deleted, renamed, untracked or conflicted.
/// </summary>
public sealed record RepositoryChange(string Path, string Status);

/// <summary>
/// 分支中文件的已提交内容；二进制或超过大小上限时 <see cref="Content"/> 为 null。A file's committed content in a branch;
/// <see cref="Content"/> is null for binary files or above the size limit.
/// </summary>
public sealed record RepositoryBlob(string? Content, long Size, bool IsBinary, bool TooLarge);

/// <summary>
/// 按目录约定管理多仓库 worktree；状态全部从磁盘和 Git 推导。Manages multi-repository worktrees by directory convention;
/// all state is derived from the disk and Git.
/// </summary>
public interface IRepositoryWorkspace
{
    Task<RepositoryWorkspaceState> ReadAsync(string root, CancellationToken ct);

    /// <summary><paramref name="name"/> 为空时从 URL 推导。Derives the name from the URL when <paramref name="name"/> is empty.</summary>
    Task CloneAsync(string root, string url, string? name, CancellationToken ct);

    /// <summary>
    /// 从各仓库的默认分支创建名为 <paramref name="feature"/> 的分支和 worktree。Creates a <paramref name="feature"/>
    /// branch and worktree from each repository's default branch.
    /// </summary>
    Task CreateFeatureAsync(
        string root,
        string feature,
        IReadOnlyList<string> repositories,
        bool addToExisting,
        CancellationToken ct
    );

    Task SwitchBranchAsync(
        string root,
        string feature,
        string repository,
        string branch,
        bool createBranch,
        CancellationToken ct
    );

    /// <summary>
    /// 用 <c>--no-ff</c> 把 worktree 的 HEAD 合并到默认分支；冲突时中止合并。Merges the worktree HEAD into the default
    /// branch with <c>--no-ff</c>; aborts the merge on conflicts.
    /// </summary>
    Task MergeAsync(string root, string feature, string repository, CancellationToken ct);

    /// <summary>拉取 origin 并快进默认分支。Fetches origin and fast-forwards the default branch.</summary>
    Task SyncAsync(string root, string repository, CancellationToken ct);

    Task RestoreDefaultBranchAsync(string root, string repository, CancellationToken ct);

    /// <summary>
    /// 列出本地或远程跟踪分支 <paramref name="branch"/> 中 <paramref name="path"/> 的直接子项（空路径为根目录）。
    /// Lists the direct children of <paramref name="path"/> (empty for the root) in the local or remote-tracking
    /// <paramref name="branch"/>.
    /// </summary>
    Task<RepositoryTree> ListTreeAsync(
        string root,
        string repository,
        string branch,
        string path,
        CancellationToken ct
    );

    /// <summary>检出 <paramref name="branch"/> 的目录中的未提交改动。Uncommitted changes where <paramref name="branch"/> is checked out.</summary>
    Task<RepositoryChange[]> ListChangesAsync(string root, string repository, string branch, CancellationToken ct);

    Task<RepositoryBlob> ReadBlobAsync(
        string root,
        string repository,
        string branch,
        string path,
        CancellationToken ct
    );
}
