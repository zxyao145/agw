using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agw.Files.Abstracts;
using Agw.Files.Infrastructure.Storage;
using Agw.Shared.Exceptions;
using CliWrap;
using CliWrap.Buffered;

namespace Agw.Files.Infrastructure.Git;

public sealed class RepositoryWorkspace : IRepositoryWorkspace
{
    private const string StatePath = ".agw/bare-repositories.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FilePathLocks _locks;
    private readonly TimeProvider _time;

    public RepositoryWorkspace(FilePathLocks locks, TimeProvider time)
    {
        _locks = locks;
        _time = time;
    }

    public async Task<RepositoryWorkspaceState> ReadAsync(string root, CancellationToken ct)
    {
        await using var lease = await _locks.AcquireEntryAsync(Path.Combine(root, StatePath), ct);
        return await ReadStateAsync(root, await LoadAsync(root, ct), ct);
    }

    public async Task<RepositoryWorkspaceState> ExecuteAsync(
        string root,
        string operation,
        RepositoryWorkspaceRequest request,
        CancellationToken ct
    )
    {
        await using var lease = await _locks.AcquireEntryAsync(Path.Combine(root, StatePath), ct);
        var catalog = await LoadAsync(root, ct);
        switch (operation)
        {
            case "clone":
                await CloneAsync(root, catalog, request, ct);
                break;
            case "feature":
                await CreateFeatureAsync(root, catalog, request, ct);
                break;
            case "switch":
            case "merge":
                await ChangeAsync(root, catalog, operation, request, ct);
                break;
            default:
                throw Invalid("Unknown repository operation.");
        }
        return await ReadStateAsync(root, catalog, ct);
    }

    private static AgwException Invalid(string message) => new(ErrorCodes.InvalidParam, message);

    private static void ValidateName(string name)
    {
        if (!Regex.IsMatch(name, @"^[\p{L}\p{N}][\p{L}\p{N}._-]{0,99}$") || name.EndsWith('.') || name.Contains(".."))
            throw Invalid(
                "Use a name of 1–100 letters, digits, dots, underscores or hyphens, starting with a letter or digit."
            );
    }

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

    private static async Task<Catalog> LoadAsync(string root, CancellationToken ct)
    {
        var path = SafePath(root, StatePath);
        if (!File.Exists(path))
            return new Catalog();
        if (new FileInfo(path).Length > 1024 * 1024)
            throw Invalid("Repository catalog exceeds 1 MiB.");
        var catalog =
            JsonSerializer.Deserialize<Catalog>(await File.ReadAllTextAsync(path, ct), JsonOptions)
            ?? throw Invalid("Repository catalog is empty.");
        foreach (var repo in catalog.Repositories)
            ValidateName(repo.Name);
        foreach (var feature in catalog.Features)
        {
            ValidateName(feature.Name);
            foreach (var repo in feature.Repositories)
                if (!catalog.Repositories.Any(item => item.Name == repo))
                    throw Invalid("Feature references an unknown repository.");
        }
        return catalog;
    }

    private static async Task SaveAsync(string root, Catalog catalog, CancellationToken ct)
    {
        var path = SafePath(root, StatePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = SafePath(root, StatePath + ".new");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(catalog, JsonOptions), ct);
        File.Move(temporary, path, true);
    }

    private static async Task EnsureIgnoreAsync(string root, CancellationToken ct)
    {
        var path = SafePath(root, ".gitignore");
        var text = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToHashSet();
        var missing = new[] { "/.worktrees/", "/.agw/" }.Where(line => !lines.Contains(line)).ToArray();
        if (missing.Length != 0)
            await File.AppendAllTextAsync(
                path,
                (text.Length > 0 && !text.EndsWith('\n') ? "\n" : "") + string.Join('\n', missing) + "\n",
                ct
            );
    }

    private async Task CloneAsync(
        string root,
        Catalog catalog,
        RepositoryWorkspaceRequest request,
        CancellationToken ct
    )
    {
        ValidateName(request.Name);
        var destination = SafePath(root, request.Name);
        if (
            catalog.Repositories.Any(repo => repo.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase))
            || Directory.Exists(destination)
            || File.Exists(destination)
        )
            throw Invalid("The repository name or destination already exists.");
        // 禁止本地路径及 Git 外部传输，保留远程仓库的标准认证。Reject local paths and Git external transports; keep normal remote authentication.
        if (
            !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "ssh" or "git")
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme == "https" && !string.IsNullOrEmpty(uri.UserInfo))
        )
            throw Invalid("Use an absolute HTTPS, SSH or Git repository URL.");
        await EnsureIgnoreAsync(root, ct);
        await GitAsync(root, ["clone", "--", request.Url, destination], ct);
        var branch = await GitAsync(destination, ["symbolic-ref", "--short", "HEAD"], ct);
        await GitAsync(destination, ["rev-parse", "--verify", "HEAD"], ct);
        catalog.Repositories.Add(new Repository(request.Name, branch));
        await SaveAsync(root, catalog, ct);
    }

    private async Task CreateFeatureAsync(
        string root,
        Catalog catalog,
        RepositoryWorkspaceRequest request,
        CancellationToken ct
    )
    {
        ValidateName(request.Name);
        var utcNow = _time.GetUtcNow();
        var date = request.Date ?? new DateOnly(utcNow.Year, utcNow.Month, utcNow.Day);
        var featureName = string.IsNullOrEmpty(request.Feature)
            ? $"{date.ToString("yyMMdd", CultureInfo.InvariantCulture)}-{request.Name}"
            : request.Feature;
        ValidateName(featureName);
        var feature = catalog.Features.SingleOrDefault(item => item.Name == featureName);
        if (feature == null && !string.IsNullOrEmpty(request.Feature))
            throw Invalid("Feature not found.");
        var names = request.Repositories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0)
            throw Invalid("Select at least one repository.");
        // 先检查全部目标，再创建任何 worktree。Validate all targets before creating any worktree.
        foreach (var name in names)
        {
            var repo = FindRepository(catalog, name);
            var main = await MainPathAsync(root, repo, ct);
            await RequireCleanAsync(main, ct);
            var target = SafePath(root, $".worktrees/{featureName}/{name}");
            if (feature?.Repositories.Contains(name) == true || Directory.Exists(target) || File.Exists(target))
                throw Invalid($"Worktree already exists: {name}.");
            var branches = await BranchesAsync(main, ct);
            if (branches.Contains(featureName))
                throw Invalid($"Branch already exists in {name}: {featureName}.");
        }
        await EnsureIgnoreAsync(root, ct);
        if (feature == null)
        {
            feature = new Feature(featureName, []);
            catalog.Features.Add(feature);
        }
        foreach (var name in names)
        {
            var repo = FindRepository(catalog, name);
            var main = await MainPathAsync(root, repo, ct);
            var target = SafePath(root, $".worktrees/{featureName}/{name}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await GitAsync(main, ["worktree", "add", "-b", featureName, "--", target, repo.DefaultBranch], ct);
            feature.Repositories.Add(name);
            // 每个成功的仓库单独保存，失败后仍能继续处理剩余仓库。Persist each success so remaining repositories can be added after a failure.
            await SaveAsync(root, catalog, ct);
        }
    }

    private static async Task ChangeAsync(
        string root,
        Catalog catalog,
        string operation,
        RepositoryWorkspaceRequest request,
        CancellationToken ct
    )
    {
        var repo = FindRepository(catalog, request.Name);
        var main = await MainPathAsync(root, repo, ct);
        var feature =
            catalog.Features.SingleOrDefault(item =>
                item.Name == request.Feature && item.Repositories.Contains(repo.Name)
            ) ?? throw Invalid("Select a repository in an existing feature.");
        var worktree = await WorktreePathAsync(root, feature.Name, repo.Name, main, ct);
        await RequireCleanAsync(worktree, ct);
        if (operation == "switch")
        {
            if (string.IsNullOrWhiteSpace(request.Branch) || request.Branch.StartsWith('-'))
                throw Invalid("A valid branch name is required.");
            await GitAsync(main, ["check-ref-format", "--branch", request.Branch], ct);
            await GitAsync(
                worktree,
                request.CreateBranch ? ["switch", "-c", request.Branch] : ["switch", request.Branch],
                ct
            );
            return;
        }
        await RequireCleanAsync(main, ct);
        var branch = await GitAsync(worktree, ["symbolic-ref", "--short", "HEAD"], ct);
        var commit = await GitAsync(worktree, ["rev-parse", "--verify", "HEAD"], ct);
        // --no-ff 保留 Merge 提交；冲突保留现场，交由用户处理。Keep a merge commit with --no-ff; preserve conflicts for the user to resolve.
        await GitAsync(main, ["merge", "--no-ff", "--no-edit", "-m", $"Merge {branch}", commit], ct);
    }

    private static Repository FindRepository(Catalog catalog, string name) =>
        catalog.Repositories.SingleOrDefault(repo => repo.Name == name) ?? throw Invalid("Repository not found.");

    private static async Task<string> MainPathAsync(string root, Repository repo, CancellationToken ct)
    {
        var path = SafePath(root, repo.Name);
        SafePath(root, $"{repo.Name}/.git");
        if (!Directory.Exists(Path.Combine(path, ".git")))
            throw Invalid($"Main checkout is missing: {repo.Name}.");
        await RequireWorktreeRootAsync(path, ct);
        if (await GitAsync(path, ["symbolic-ref", "--short", "HEAD"], ct) != repo.DefaultBranch)
            throw Invalid(
                $"{repo.Name} must remain on its default branch {repo.DefaultBranch}. Restore that branch in your terminal."
            );
        return path;
    }

    private static async Task<string> WorktreePathAsync(
        string root,
        string feature,
        string name,
        string main,
        CancellationToken ct
    )
    {
        var path = SafePath(root, $".worktrees/{feature}/{name}");
        SafePath(root, $".worktrees/{feature}/{name}/.git");
        await RequireWorktreeRootAsync(path, ct);
        var common = await GitAsync(path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct);
        if (
            !Path.GetFullPath(common)
                .Equals(
                    Path.Combine(main, ".git"),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
                )
        )
            throw Invalid("Worktree does not belong to its managed repository.");
        return path;
    }

    private static async Task RequireWorktreeRootAsync(string path, CancellationToken ct)
    {
        var topLevel = await GitAsync(path, ["rev-parse", "--show-toplevel"], ct);
        if (
            !Path.GetFullPath(topLevel)
                .Equals(
                    Path.GetFullPath(path),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
                )
        )
            throw Invalid("Repository worktree root does not match its managed path.");
    }

    private static async Task RequireCleanAsync(string path, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(await GitAsync(path, ["status", "--porcelain"], ct)))
            throw Invalid($"Commit or move local changes before continuing: {path}.");
        foreach (
            var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply" }
        )
        {
            var markerPath = await GitAsync(path, ["rev-parse", "--git-path", marker], ct);
            var fullPath = Path.GetFullPath(markerPath, path);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
                throw Invalid($"Finish the current Git operation before continuing: {path}.");
        }
    }

    private static async Task<string[]> BranchesAsync(string path, CancellationToken ct) =>
        (await GitAsync(path, ["for-each-ref", "--format=%(refname:short)", "refs/heads/"], ct)).Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries
        );

    private static async Task<RepositoryWorkspaceState> ReadStateAsync(
        string root,
        Catalog catalog,
        CancellationToken ct
    )
    {
        var repositories = new List<WorkspaceRepository>();
        var features = new List<WorkspaceFeature>();
        foreach (var repo in catalog.Repositories)
        {
            var main = await MainPathAsync(root, repo, ct);
            repositories.Add(
                new()
                {
                    Name = repo.Name,
                    DefaultBranch = repo.DefaultBranch,
                    Branch = repo.DefaultBranch,
                    Dirty = !string.IsNullOrEmpty(await GitAsync(main, ["status", "--porcelain"], ct)),
                }
            );
        }
        foreach (var feature in catalog.Features)
        {
            var entries = new List<FeatureRepository>();
            foreach (var name in feature.Repositories)
            {
                var main = await MainPathAsync(root, FindRepository(catalog, name), ct);
                var path = await WorktreePathAsync(root, feature.Name, name, main, ct);
                entries.Add(
                    new()
                    {
                        Name = name,
                        Branch = await GitAsync(path, ["rev-parse", "--abbrev-ref", "HEAD"], ct),
                        Dirty = !string.IsNullOrEmpty(await GitAsync(path, ["status", "--porcelain"], ct)),
                        Branches = await BranchesAsync(main, ct),
                    }
                );
            }
            features.Add(new(feature.Name, entries.ToArray()));
        }
        return new(repositories.ToArray(), features.ToArray());
    }

    private static async Task<string> GitAsync(string directory, string[] arguments, CancellationToken ct)
    {
        var result = await Cli.Wrap("git")
            .WithWorkingDirectory(directory)
            .WithArguments(
                new[] { "-c", "protocol.file.allow=never", "-c", "protocol.ext.allow=never" }.Concat(arguments)
            )
            .WithEnvironmentVariables(
                new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_EDITOR"] = "true" }
            )
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (result.ExitCode != 0)
            throw Invalid($"Git {arguments[0]} failed: {result.StandardError.Trim()} {result.StandardOutput.Trim()}");
        return result.StandardOutput.Trim();
    }

    private sealed class Catalog
    {
        public List<Repository> Repositories { get; init; } = [];
        public List<Feature> Features { get; init; } = [];
    }

    private sealed record Repository(string Name, string DefaultBranch);

    private sealed record Feature(string Name, List<string> Repositories);
}
