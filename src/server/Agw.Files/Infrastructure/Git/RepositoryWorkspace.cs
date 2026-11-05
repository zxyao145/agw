using System.Globalization;
using System.Text.RegularExpressions;
using Agw.Files.Abstracts;
using Agw.Files.Infrastructure.Storage;
using Agw.Shared.Exceptions;
using CliWrap;
using CliWrap.Buffered;

namespace Agw.Files.Infrastructure.Git;

public sealed partial class RepositoryWorkspace : IRepositoryWorkspace
{
    private const string WorktreesDirectory = ".worktrees";
    private const long MaxBlobBytes = 2 * 1024 * 1024;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private readonly FilePathLocks _locks;

    public RepositoryWorkspace(FilePathLocks locks) => _locks = locks;

    public async Task<RepositoryWorkspaceState> ReadAsync(string root, CancellationToken ct)
    {
        var names = ListDirectories(root).Where(name => IsDirectory(Path.Combine(root, name, ".git")));
        var repositories = await Task.WhenAll(names.Select(name => ReadRepositoryAsync(root, name, ct)));
        var defaults = repositories
            .Where(repo => repo.Error == null)
            .ToDictionary(repo => repo.Name, repo => repo.DefaultBranch);
        var features = new List<WorkspaceFeature>();
        foreach (var feature in ListDirectories(SafePath(root, WorktreesDirectory)))
        {
            var entries = ListDirectories(Path.Combine(root, WorktreesDirectory, feature))
                .Select(name => ReadWorktreeAsync(root, feature, name, defaults, ct));
            features.Add(new(feature, await Task.WhenAll(entries)));
        }
        return new(repositories, features.ToArray());
    }

    public async Task CloneAsync(string root, string url, string? name, CancellationToken ct, string? proxyUrl = null)
    {
        var derived = ValidateUrl(url);
        name = string.IsNullOrWhiteSpace(name) ? derived : name.Trim();
        ValidateName(name);
        await using var lease = await LockAsync(root, ct);
        var destination = SafePath(root, name);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw Conflict($"{name} already exists in the project.");
        try
        {
            await GitAsync(root, ["clone", "--", url, destination], ct, ErrorCodes.GitRemoteOperationFailed, proxyUrl);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DeletePartialClone(destination);
            throw;
        }
    }

    private static void DeletePartialClone(string destination)
    {
        if (!Directory.Exists(destination))
            return;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };
        foreach (var file in Directory.EnumerateFiles(destination, "*", options))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(destination, recursive: true);
    }

    public async Task CreateFeatureAsync(
        string root,
        string feature,
        IReadOnlyList<string> repositories,
        bool addToExisting,
        CancellationToken ct
    )
    {
        ValidateName(feature);
        var names = repositories.Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0)
            throw Invalid("Select at least one repository.");
        await using var lease = await LockAsync(root, ct);
        var exists = Directory.Exists(SafePath(root, $"{WorktreesDirectory}/{feature}"));
        if (exists && !addToExisting)
            throw Conflict($"Feature {feature} already exists.");
        if (!exists && addToExisting)
            throw new AgwException(ErrorCodes.ResourceNotFound, $"Feature {feature} was not found.");
        // 先检查全部目标，再创建任何 worktree。Validate all targets before creating any worktree.
        var targets = new List<(string Main, string Target, string DefaultBranch)>();
        foreach (var name in names)
        {
            var main = await MainPathAsync(root, name, ct);
            await GitAsync(main, ["check-ref-format", "--branch", feature], ct, ErrorCodes.InvalidParam);
            var target = SafePath(root, $"{WorktreesDirectory}/{feature}/{name}");
            if (Directory.Exists(target) || File.Exists(target))
                throw Conflict($"Worktree already exists: {name}.");
            if ((await RunGitAsync(main, ["show-ref", "--verify", "--quiet", $"refs/heads/{feature}"], ct)).IsSuccess)
                throw Conflict($"Branch {feature} already exists in {name}.");
            targets.Add((main, target, await DefaultBranchAsync(main, ct)));
        }
        await EnsureIgnoreAsync(root, ct);
        foreach (var (main, target, defaultBranch) in targets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await GitAsync(main, ["worktree", "add", "-b", feature, "--", target, defaultBranch], ct);
        }
    }

    public async Task DeleteFeatureAsync(string root, string feature, CancellationToken ct)
    {
        ValidateName(feature);
        await using var lease = await LockAsync(root, ct);
        var directory = SafePath(root, $"{WorktreesDirectory}/{feature}");
        if (!Directory.Exists(directory))
            throw new AgwException(ErrorCodes.ResourceNotFound, $"Feature {feature} was not found.");
        var targets = new List<(string Main, string Worktree)>();
        // 删除前检查全部 worktrees。Check every worktree before removing any.
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            var target = SafePath(root, $"{WorktreesDirectory}/{feature}/{name}");
            if (!Directory.Exists(target))
                throw Conflict($"Move files outside the worktrees before deleting feature {feature}: {name}.");
            var main = await MainPathAsync(root, name, ct);
            var worktree = await WorktreePathAsync(root, feature, name, ct);
            await RequireRemovableWorktreeAsync(worktree, ct);
            targets.Add((main, worktree));
        }
        foreach (var (main, worktree) in targets)
            await GitAsync(main, ["worktree", "remove", "--", worktree], ct);
        Directory.Delete(directory);
    }

    private static async Task RequireRemovableWorktreeAsync(string worktree, CancellationToken ct)
    {
        await RequireCleanAsync(worktree, ct);
        var changes = await GitAsync(
            worktree,
            ["--no-optional-locks", "status", "--porcelain=v1", "--untracked-files=all", "--ignore-submodules=none"],
            ct
        );
        if (changes.Length > 0)
            throw Conflict($"Commit or stash local changes before deleting this worktree: {worktree}.");
        if (await HasGitPathAsync(worktree, "locked", ct))
            throw Conflict($"Unlock this worktree before deleting its feature: {worktree}.");
        var submodules = await GitAsync(worktree, ["submodule", "foreach", "--quiet", "echo initialized"], ct);
        if (submodules.Length > 0)
            throw Conflict($"Deinitialize submodules before deleting this worktree: {worktree}.");
    }

    public async Task SwitchBranchAsync(
        string root,
        string feature,
        string repository,
        string branch,
        bool createBranch,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-'))
            throw Invalid("A valid branch name is required.");
        await using var lease = await LockAsync(root, ct);
        var worktree = await WorktreePathAsync(root, feature, repository, ct);
        await RequireCleanAsync(worktree, ct);
        await GitAsync(worktree, ["check-ref-format", "--branch", branch], ct, ErrorCodes.InvalidParam);
        await GitAsync(worktree, createBranch ? ["switch", "-c", branch] : ["switch", branch], ct);
    }

    public async Task MergeAsync(string root, string feature, string repository, CancellationToken ct)
    {
        await using var lease = await LockAsync(root, ct);
        var main = await RequireOnDefaultBranchAsync(root, repository, ct);
        await RequireCleanAsync(main, ct);
        var worktree = await WorktreePathAsync(root, feature, repository, ct);
        await RequireCleanAsync(worktree, ct);
        var commit = await GitAsync(worktree, ["rev-parse", "--verify", "HEAD"], ct);
        var branch = await GitAsync(worktree, ["branch", "--show-current"], ct);
        var label = branch.Length > 0 ? branch : commit[..12];
        var result = await RunGitAsync(main, ["merge", "--no-ff", "--no-edit", "-m", $"Merge {label}", commit], ct);
        if (result.IsSuccess)
            return;
        if (!await HasGitPathAsync(main, "MERGE_HEAD", ct))
            throw Failure(ErrorCodes.RepositoryStateConflict, "merge", result);
        // 不把冲突留在主工作副本：中止合并，由用户在 worktree 中解决。Never leave conflicts in the root checkout:
        // abort and let the user resolve them in the worktree.
        var conflicts = await GitAsync(main, ["diff", "--name-only", "--diff-filter=U"], ct);
        await GitAsync(main, ["merge", "--abort"], ct);
        if (conflicts.Length == 0)
            throw Failure(ErrorCodes.RepositoryStateConflict, "merge", result);
        throw new AgwException(
            ErrorCodes.RepositoryMergeConflict,
            $"Merging {label} into {repository} conflicts in: {string.Join(", ", conflicts.Split('\n'))}. "
                + "Merge the default branch into the feature branch and resolve the conflicts there."
        );
    }

    public async Task SyncAsync(string root, string repository, CancellationToken ct, string? proxyUrl = null)
    {
        await using var lease = await LockAsync(root, ct);
        var main = await RequireOnDefaultBranchAsync(root, repository, ct);
        var defaultBranch = await DefaultBranchAsync(main, ct);
        await GitAsync(main, ["fetch", "--prune", "origin"], ct, ErrorCodes.GitRemoteOperationFailed, proxyUrl);
        await GitAsync(main, ["merge", "--ff-only", $"refs/remotes/origin/{defaultBranch}"], ct);
    }

    public async Task RestoreDefaultBranchAsync(string root, string repository, CancellationToken ct)
    {
        await using var lease = await LockAsync(root, ct);
        var main = await MainPathAsync(root, repository, ct);
        await RequireCleanAsync(main, ct);
        await GitAsync(main, ["switch", await DefaultBranchAsync(main, ct)], ct);
    }

    public async Task<RepositoryTree> ListTreeAsync(
        string root,
        string repository,
        string branch,
        string path,
        CancellationToken ct
    )
    {
        var segments = SplitRelativePath(path);
        var main = await MainPathAsync(root, repository, ct);
        var reference = await ResolveBranchAsync(main, branch, ct);
        var checkout = await FindCheckoutAsync(root, repository, main, reference, ct);
        if (checkout != null)
        {
            var directory = SafePath(root, string.Join('/', [checkout, .. segments]));
            if (!Directory.Exists(directory))
                throw new AgwException(ErrorCodes.ResourceNotFound, $"Directory not found: {path}.");
            var entries = new DirectoryInfo(directory)
                .EnumerateFileSystemInfos()
                .Where(item => segments.Length > 0 || item.Name != ".git")
                .Select(item => new RepositoryTreeEntry(
                    item.Name,
                    string.Join('/', [.. segments, item.Name]),
                    item is DirectoryInfo && !item.Attributes.HasFlag(FileAttributes.ReparsePoint)
                ));
            return new(checkout, Sort(entries));
        }
        var result = await RunGitAsync(
            main,
            segments.Length == 0
                ? ["ls-tree", "-z", reference]
                : ["--literal-pathspecs", "ls-tree", "-z", reference, "--", $"{string.Join('/', segments)}/"],
            ct
        );
        if (!result.IsSuccess)
            throw Failure(ErrorCodes.RepositoryStateConflict, "ls-tree", result);
        var items = result
            .StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var tab = line.IndexOf('\t');
                var fullPath = line[(tab + 1)..];
                return new RepositoryTreeEntry(fullPath.Split('/')[^1], fullPath, line.Split(' ')[1] == "tree");
            })
            .ToArray();
        if (items.Length == 0 && segments.Length > 0)
            throw new AgwException(ErrorCodes.ResourceNotFound, $"Directory not found: {path}.");
        return new(null, Sort(items));
    }

    public async Task<RepositoryChange[]> ListChangesAsync(
        string root,
        string repository,
        string branch,
        CancellationToken ct
    )
    {
        var main = await MainPathAsync(root, repository, ct);
        var checkout = await FindCheckoutAsync(root, repository, main, await ResolveBranchAsync(main, branch, ct), ct);
        if (checkout == null)
            return [];
        var output = (
            await RunGitAsync(
                SafePath(root, checkout),
                ["--no-optional-locks", "status", "--porcelain=v1", "-z", "--untracked-files=all"],
                ct
            )
        ).StandardOutput;
        var tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var changes = new List<RepositoryChange>();
        for (var index = 0; index < tokens.Length; index++)
        {
            var code = tokens[index][..2];
            changes.Add(new(tokens[index][3..], ChangeStatus(code)));
            // 重命名与复制的下一项是原路径。The next token of a rename or copy is the original path.
            if (code[0] is 'R' or 'C')
                index++;
        }
        return changes.OrderBy(change => change.Path, StringComparer.Ordinal).ToArray();
    }

    public async Task<RepositoryBlob> ReadBlobAsync(
        string root,
        string repository,
        string branch,
        string path,
        CancellationToken ct
    )
    {
        if (SplitRelativePath(path).Length == 0)
            throw Invalid("A file path is required.");
        var main = await MainPathAsync(root, repository, ct);
        var objectName = $"{await ResolveBranchAsync(main, branch, ct)}:{path}";
        var type = await RunGitAsync(main, ["cat-file", "-t", objectName], ct);
        if (!type.IsSuccess || type.StandardOutput.Trim() != "blob")
            throw new AgwException(ErrorCodes.ResourceNotFound, $"File not found: {path}.");
        var size = long.Parse(await GitAsync(main, ["cat-file", "-s", objectName], ct), CultureInfo.InvariantCulture);
        if (size > MaxBlobBytes)
            return new(null, size, false, true);
        var content = (await RunGitAsync(main, ["cat-file", "blob", objectName], ct)).StandardOutput;
        return content.AsSpan(0, Math.Min(content.Length, 8000)).Contains('\0')
            ? new(null, size, true, false)
            : new(content, size, false, false);
    }

    /// <summary>
    /// 本地分支解析为 <c>refs/heads/</c>，否则为 <c>refs/remotes/</c>。Resolves to <c>refs/heads/</c> for a local
    /// branch, otherwise <c>refs/remotes/</c>.
    /// </summary>
    private static async Task<string> ResolveBranchAsync(string main, string branch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-'))
            throw Invalid("A valid branch name is required.");
        foreach (var reference in new[] { $"refs/heads/{branch}", $"refs/remotes/{branch}" })
            if ((await RunGitAsync(main, ["show-ref", "--verify", "--quiet", reference], ct)).IsSuccess)
                return reference;
        throw new AgwException(ErrorCodes.ResourceNotFound, $"Branch not found: {branch}.");
    }

    /// <summary>
    /// 检出本地分支的主工作副本或 feature worktree（相对 Project 根目录）。The root checkout or feature worktree, relative
    /// to the Project root, where a local branch is checked out.
    /// </summary>
    private static async Task<string?> FindCheckoutAsync(
        string root,
        string repository,
        string main,
        string reference,
        CancellationToken ct
    )
    {
        if (!reference.StartsWith("refs/heads/", StringComparison.Ordinal))
            return null;
        var branch = reference["refs/heads/".Length..];
        if (await GitAsync(main, ["branch", "--show-current"], ct) == branch)
            return repository;
        foreach (var feature in ListDirectories(SafePath(root, WorktreesDirectory)))
        {
            if (!Directory.Exists(Path.Combine(root, WorktreesDirectory, feature, repository)))
                continue;
            try
            {
                var path = await WorktreePathAsync(root, feature, repository, ct);
                if (await GitAsync(path, ["branch", "--show-current"], ct) == branch)
                    return $"{WorktreesDirectory}/{feature}/{repository}";
            }
            catch (AgwException)
            {
                // 无法识别的目录不是该分支的检出位置。An unrecognized directory is not where the branch is checked out.
            }
        }
        return null;
    }

    private static string[] SplitRelativePath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(part => part is "." or ".." || part.Contains('\\')) || path.StartsWith('/'))
            throw Invalid("Invalid repository path.");
        return segments;
    }

    private static RepositoryTreeEntry[] Sort(IEnumerable<RepositoryTreeEntry> entries) =>
        entries
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();

    private static string ChangeStatus(string code) =>
        code switch
        {
            "??" => "untracked",
            "DD" or "AA" => "conflicted",
            _ when code.Contains('U') => "conflicted",
            _ when code.Contains('D') => "deleted",
            _ when code.Contains('R') => "renamed",
            _ when code.Contains('A') => "added",
            _ => "modified",
        };

    private Task<FilePathLease> LockAsync(string root, CancellationToken ct) =>
        _locks.AcquireEntryAsync(Path.Combine(root, WorktreesDirectory), ct);

    private static async Task<WorkspaceRepository> ReadRepositoryAsync(string root, string name, CancellationToken ct)
    {
        try
        {
            var path = await MainPathAsync(root, name, ct);
            var status = await StatusAsync(path, ct);
            return new()
            {
                Name = name,
                DefaultBranch = await DefaultBranchAsync(path, ct),
                Branch = status.Branch,
                Dirty = status.Dirty,
                Ahead = status.Ahead,
                Behind = status.Behind,
                Branches = (
                    await GitAsync(path, ["for-each-ref", "--format=%(refname:short)", "refs/heads/"], ct)
                ).Split('\n', StringSplitOptions.RemoveEmptyEntries),
                RemoteBranches = (await GitAsync(path, ["for-each-ref", "--format=%(refname)", "refs/remotes/"], ct))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(name => !name.EndsWith("/HEAD", StringComparison.Ordinal))
                    .Select(name => name["refs/remotes/".Length..])
                    .ToArray(),
            };
        }
        catch (AgwException exception)
        {
            return new() { Name = name, Error = exception.Message };
        }
    }

    private static async Task<FeatureRepository> ReadWorktreeAsync(
        string root,
        string feature,
        string name,
        IReadOnlyDictionary<string, string> defaults,
        CancellationToken ct
    )
    {
        try
        {
            if (!defaults.TryGetValue(name, out var defaultBranch))
                throw new AgwException(ErrorCodes.ResourceNotFound, $"No readable root checkout named {name}.");
            var path = await WorktreePathAsync(root, feature, name, ct);
            var status = await StatusAsync(path, ct);
            var counts = (
                await GitAsync(path, ["rev-list", "--left-right", "--count", $"refs/heads/{defaultBranch}...HEAD"], ct)
            ).Split('\t');
            return new()
            {
                Name = name,
                Branch = status.Branch,
                Dirty = status.Dirty,
                Behind = int.Parse(counts[0], CultureInfo.InvariantCulture),
                Ahead = int.Parse(counts[1], CultureInfo.InvariantCulture),
            };
        }
        catch (AgwException exception)
        {
            return new() { Name = name, Error = exception.Message };
        }
    }

    private static async Task<Status> StatusAsync(string path, CancellationToken ct)
    {
        var lines = (await GitAsync(path, ["--no-optional-locks", "status", "--porcelain=v2", "--branch"], ct)).Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries
        );
        var head = lines.FirstOrDefault(line => line.StartsWith("# branch.head ", StringComparison.Ordinal))?[14..];
        var counts = lines
            .FirstOrDefault(line => line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            ?[12..].Split(' ');
        return new(
            head is null or "(detached)" ? "" : head,
            lines.Any(line => !line.StartsWith('#')),
            counts == null ? null : int.Parse(counts[0][1..], CultureInfo.InvariantCulture),
            counts == null ? null : int.Parse(counts[1][1..], CultureInfo.InvariantCulture)
        );
    }

    private static async Task<string> DefaultBranchAsync(string main, CancellationToken ct)
    {
        var remote = await RunGitAsync(main, ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], ct);
        var name = remote.StandardOutput.Trim();
        if (remote.IsSuccess && name.StartsWith("origin/", StringComparison.Ordinal))
            return name["origin/".Length..];
        var current = await GitAsync(main, ["branch", "--show-current"], ct);
        return current.Length > 0
            ? current
            : throw Conflict("Cannot determine the default branch: origin/HEAD is missing and HEAD is detached.");
    }

    private static async Task<string> RequireOnDefaultBranchAsync(string root, string name, CancellationToken ct)
    {
        var main = await MainPathAsync(root, name, ct);
        var current = await GitAsync(main, ["branch", "--show-current"], ct);
        var defaultBranch = await DefaultBranchAsync(main, ct);
        if (current != defaultBranch)
            throw Conflict($"{name} is not on its default branch {defaultBranch}. Restore the default branch first.");
        return main;
    }

    private static async Task<string> MainPathAsync(string root, string name, CancellationToken ct)
    {
        ValidateName(name);
        var path = SafePath(root, name);
        if (!IsDirectory(SafePath(root, $"{name}/.git")))
            throw new AgwException(ErrorCodes.ResourceNotFound, $"No root checkout named {name}.");
        await RequireWorktreeRootAsync(path, ct);
        return path;
    }

    private static async Task<string> WorktreePathAsync(string root, string feature, string name, CancellationToken ct)
    {
        ValidateName(feature);
        ValidateName(name);
        var main = SafePath(root, name);
        var path = SafePath(root, $"{WorktreesDirectory}/{feature}/{name}");
        if (!File.Exists(SafePath(root, $"{WorktreesDirectory}/{feature}/{name}/.git")))
            throw new AgwException(ErrorCodes.ResourceNotFound, $"No worktree for {name} in feature {feature}.");
        await RequireWorktreeRootAsync(path, ct);
        var common = await GitAsync(path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct);
        if (!Path.GetFullPath(common).Equals(Path.Combine(main, ".git"), PathComparison))
            throw Conflict(
                $"The worktree for {name} in feature {feature} does not belong to the root checkout {name}."
            );
        return path;
    }

    private static async Task RequireWorktreeRootAsync(string path, CancellationToken ct)
    {
        var topLevel = await GitAsync(path, ["rev-parse", "--show-toplevel"], ct);
        if (!Path.GetFullPath(topLevel).Equals(Path.GetFullPath(path), PathComparison))
            throw Conflict("Repository worktree root does not match its managed path.");
    }

    private static async Task RequireCleanAsync(string path, CancellationToken ct)
    {
        if ((await StatusAsync(path, ct)).Dirty)
            throw Conflict($"Commit or stash local changes before continuing: {path}.");
        foreach (
            var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply" }
        )
            if (await HasGitPathAsync(path, marker, ct))
                throw Conflict($"Finish the current Git operation before continuing: {path}.");
    }

    private static async Task<bool> HasGitPathAsync(string path, string name, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(await GitAsync(path, ["rev-parse", "--git-path", name], ct), path);
        return File.Exists(fullPath) || Directory.Exists(fullPath);
    }

    private static async Task EnsureIgnoreAsync(string root, CancellationToken ct)
    {
        var path = SafePath(root, ".gitignore");
        var text = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
        const string rule = "/.worktrees/";
        if (!text.Split('\n').Any(line => line.TrimEnd('\r') == rule))
            await File.AppendAllTextAsync(
                path,
                (text.Length > 0 && !text.EndsWith('\n') ? "\n" : "") + rule + "\n",
                ct
            );
    }

    /// <summary>
    /// 只接受远程 URL，返回从中推导的仓库名。Accepts only remote URLs and returns the repository name derived from it.
    /// </summary>
    private static string ValidateUrl(string url)
    {
        url = url.Trim();
        string path;
        // 禁止本地路径及 Git 外部传输，保留远程仓库的标准认证。Reject local paths and Git external transports; keep normal remote authentication.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh" or "git")
        {
            if (!string.IsNullOrEmpty(uri.Fragment) || (uri.Scheme == "https" && !string.IsNullOrEmpty(uri.UserInfo)))
                throw Invalid("Remove credentials and fragments from the repository URL.");
            path = uri.AbsolutePath;
        }
        else if (ScpLikeUrl().Match(url) is { Success: true } match)
            path = match.Groups["path"].Value;
        else
            throw Invalid("Use an HTTPS, SSH (ssh:// or user@host:path) or Git repository URL.");
        var name = path.TrimEnd('/').Split('/')[^1];
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static void ValidateName(string name)
    {
        if (!ValidName().IsMatch(name) || name.EndsWith('.') || name.Contains(".."))
            throw Invalid(
                "Use a name of 1–100 letters, digits, dots, underscores or hyphens, starting with a letter or digit."
            );
    }

    private static bool IsDirectory(string path) =>
        Directory.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static string[] ListDirectories(string path) =>
        !Directory.Exists(path)
            ? []
            : new DirectoryInfo(path)
                .EnumerateDirectories()
                .Where(item => !item.Attributes.HasFlag(FileAttributes.ReparsePoint) && !item.Name.StartsWith('.'))
                .Select(item => item.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();

    private static string SafePath(string root, string relative)
    {
        var path = Path.GetFullPath(root);
        foreach (var part in relative.Split('/'))
        {
            if (part is "" or "." or ".." || part.Contains('\\') || Path.IsPathRooted(part))
                throw Invalid("Invalid workspace path.");
            path = Path.Combine(path, part);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw Invalid("Repository workspace paths must not contain symbolic links.");
        }
        return path;
    }

    private static AgwException Invalid(string message) => new(ErrorCodes.InvalidParam, message);

    private static AgwException Conflict(string message) => new(ErrorCodes.RepositoryStateConflict, message);

    private static AgwException Failure(ErrorCode code, string command, BufferedCommandResult result) =>
        new(code, $"git {command} failed: {result.StandardError.Trim()} {result.StandardOutput.Trim()}".Trim());

    private static async Task<string> GitAsync(
        string directory,
        string[] arguments,
        CancellationToken ct,
        ErrorCode? failure = null,
        string? proxyUrl = null
    )
    {
        var result = await RunGitAsync(directory, arguments, ct, proxyUrl);
        if (!result.IsSuccess)
            throw Failure(
                failure ?? ErrorCodes.RepositoryStateConflict,
                arguments.First(argument => !argument.StartsWith('-')),
                result
            );
        return result.StandardOutput.Trim();
    }

    private static Task<BufferedCommandResult> RunGitAsync(
        string directory,
        string[] arguments,
        CancellationToken ct,
        string? proxyUrl = null
    ) =>
        Cli.Wrap("git")
            .WithWorkingDirectory(directory)
            .WithArguments(
                new[] { "-c", "protocol.file.allow=never", "-c", "protocol.ext.allow=never" }
                    .Concat(ProxyArguments(proxyUrl))
                    .Concat(arguments)
            )
            .WithEnvironmentVariables(
                new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_EDITOR"] = "true" }
            )
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct)
            .Task;

    private static string[] ProxyArguments(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
            return [];
        proxyUrl = proxyUrl.Trim();
        if (
            proxyUrl.Any(char.IsControl)
            || !Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri)
            || uri.Host.Length == 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5" or "socks5h")
        )
            throw Invalid("Use an HTTP, HTTPS or SOCKS proxy URL with a host and optional port.");
        return ["-c", $"http.proxy={proxyUrl}", "-c", $"remote.origin.proxy={proxyUrl}"];
    }

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N}._-]{0,99}$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+@[A-Za-z0-9][A-Za-z0-9.-]*:(?<path>[A-Za-z0-9._~][A-Za-z0-9._~/-]*)$")]
    private static partial Regex ScpLikeUrl();

    private sealed record Status(string Branch, bool Dirty, int? Ahead, int? Behind);
}
