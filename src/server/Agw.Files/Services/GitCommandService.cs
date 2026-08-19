using System.Collections.Concurrent;
using Agw.Files.Utils;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace Agw.Files.Services;

public enum GitDiffScope
{
    All,
    Staged,
    Unstaged,
}

public sealed record GitFileStatus(string? StagedStatus, string? UnstagedStatus)
{
    public string? AggregateStatus
    {
        get
        {
            if (StagedStatus == "deleted" || UnstagedStatus == "deleted")
            {
                return "deleted";
            }

            if (StagedStatus == "added" || UnstagedStatus == "added")
            {
                return "added";
            }

            if (StagedStatus == "untracked" || UnstagedStatus == "untracked")
            {
                return "untracked";
            }

            return StagedStatus ?? UnstagedStatus;
        }
    }

    public string? GetStatus(GitDiffScope scope) =>
        scope switch
        {
            GitDiffScope.Staged => StagedStatus,
            GitDiffScope.Unstaged => UnstagedStatus,
            _ => AggregateStatus,
        };
}

public record GitChangedFiles(Dictionary<string, GitFileStatus> FileStatuses, HashSet<string> DeletedFiles);

public record GitDiffResult(bool Success, string Diff, bool Unchanged, string? OriginalContent, string? Error);

public record GitResetResult(bool Success, string Message, string? Error, bool IsClientError);

public record GitIndexResult(bool Success, string Message, string? Error, bool IsClientError);

public record GitCloneResult(bool Success, string? Error, string? Stdout, string? Stderr);

public class GitCommandService : IGitCommandService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> IndexMutationLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
    );

    private readonly ILogger<GitCommandService> _logger;

    public GitCommandService(ILogger<GitCommandService> logger)
    {
        _logger = logger;
    }

    public async Task<GitChangedFiles?> GetChangedFilesAsync(
        string directory,
        CancellationToken cancellationToken = default
    )
    {
        var gitDirectory = FindGitDirectory(directory);
        if (gitDirectory == null)
        {
            return null;
        }

        try
        {
            var result = await RunGitAsync(gitDirectory, ["status", "--porcelain"], cancellationToken);

            if (result.ExitCode != 0)
            {
                return null;
            }

            var fileStatuses = new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase);
            var deletedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.Length < 3)
                    continue;

                var statusCode = line.Substring(0, 2);
                var filename = line.Substring(3).Trim().Trim('"');
                var fullPath = Path.GetFullPath(Path.Combine(gitDirectory, filename));

                GitFileStatus status;
                if (statusCode == "??")
                {
                    status = new GitFileStatus(null, "untracked");
                }
                else
                {
                    status = new GitFileStatus(MapStatus(statusCode[0]), MapStatus(statusCode[1]));
                }

                if (status.StagedStatus == "deleted" || status.UnstagedStatus == "deleted")
                {
                    deletedFiles.Add(fullPath);
                }

                fileStatuses[fullPath] = status;
            }

            return new GitChangedFiles(fileStatuses, deletedFiles);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get changed files from git");
            return null;
        }
    }

    public async Task<GitDiffResult> GetDiffAsync(
        string filePath,
        CancellationToken cancellationToken = default,
        GitDiffScope scope = GitDiffScope.All
    )
    {
        var gitDirectory = FindGitDirectory(filePath);
        if (gitDirectory == null)
        {
            return new GitDiffResult(false, string.Empty, false, null, "File is not in a git repository");
        }

        IReadOnlyCollection<string> diffArguments = scope switch
        {
            GitDiffScope.Staged => ["diff", "--cached", "HEAD", "--", filePath],
            GitDiffScope.Unstaged => ["diff", "--", filePath],
            _ => ["diff", "HEAD", "--", filePath],
        };
        var diffResult = await RunGitAsync(gitDirectory, diffArguments, cancellationToken);
        if (diffResult.ExitCode != 0 && !string.IsNullOrEmpty(diffResult.StandardError))
        {
            return new GitDiffResult(false, string.Empty, false, null, diffResult.StandardError);
        }

        if (!string.IsNullOrWhiteSpace(diffResult.StandardOutput))
        {
            return new GitDiffResult(true, diffResult.StandardOutput, false, null, null);
        }

        if (scope == GitDiffScope.Unstaged)
        {
            var untrackedDiff = await GetUntrackedDiffAsync(gitDirectory, filePath, cancellationToken);
            if (untrackedDiff != null)
            {
                return untrackedDiff;
            }
        }

        var gitRootResult = await RunGitAsync(gitDirectory, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (gitRootResult.ExitCode != 0)
        {
            return new GitDiffResult(false, string.Empty, false, null, "Failed to get git root directory");
        }

        var gitRoot = gitRootResult.StandardOutput.Trim();
        var relativePath = Path.GetRelativePath(gitRoot, filePath).Replace("\\", "/");
        var baseline = scope == GitDiffScope.Unstaged ? $":{relativePath}" : $"HEAD:{relativePath}";
        var showResult = await RunGitAsync(gitDirectory, ["show", baseline], cancellationToken);
        if (showResult.ExitCode == 0)
        {
            return new GitDiffResult(true, string.Empty, true, showResult.StandardOutput, null);
        }

        return new GitDiffResult(true, string.Empty, false, null, null);
    }

    private static string? MapStatus(char statusCode) =>
        statusCode switch
        {
            ' ' => null,
            'A' => "added",
            'D' => "deleted",
            '?' => "untracked",
            _ => "modified",
        };

    private static async Task<GitDiffResult?> GetUntrackedDiffAsync(
        string gitDirectory,
        string filePath,
        CancellationToken cancellationToken
    )
    {
        var untrackedResult = await RunGitAsync(
            gitDirectory,
            ["ls-files", "--others", "--exclude-standard", "--", filePath],
            cancellationToken
        );
        if (untrackedResult.ExitCode != 0 || string.IsNullOrWhiteSpace(untrackedResult.StandardOutput))
        {
            return null;
        }

        var emptyFile = Path.GetTempFileName();
        try
        {
            var diffResult = await RunGitAsync(
                gitDirectory,
                ["diff", "--no-index", "--", emptyFile, filePath],
                cancellationToken
            );
            if (string.IsNullOrWhiteSpace(diffResult.StandardOutput))
            {
                if (diffResult.ExitCode == 0)
                {
                    return new GitDiffResult(true, string.Empty, false, null, null);
                }

                return new GitDiffResult(false, string.Empty, false, null, diffResult.StandardError);
            }

            return new GitDiffResult(true, diffResult.StandardOutput, false, null, null);
        }
        finally
        {
            File.Delete(emptyFile);
        }
    }

    public async Task<GitResetResult> ResetFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var gitDirectory = FindGitDirectory(filePath);
        if (gitDirectory == null)
        {
            return new GitResetResult(false, "File is not in a git repository", null, true);
        }

        var gitRootResult = await RunGitAsync(gitDirectory, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (gitRootResult.ExitCode != 0)
        {
            return new GitResetResult(false, "Failed to get git root directory", gitRootResult.StandardError, false);
        }

        var gitRoot = gitRootResult.StandardOutput.Trim();
        var relativePath = Path.GetRelativePath(gitRoot, filePath).Replace("\\", "/");
        var statusResult = await RunGitAsync(gitDirectory, ["status", "--porcelain", relativePath], cancellationToken);
        if (statusResult.ExitCode != 0)
        {
            return new GitResetResult(false, "Failed to check git status", statusResult.StandardError, true);
        }

        if (string.IsNullOrWhiteSpace(statusResult.StandardOutput))
        {
            return new GitResetResult(false, "File has no modifications to reset", null, true);
        }

        var resetResult = await RunGitAsync(gitDirectory, ["checkout", "HEAD", "--", relativePath], cancellationToken);
        if (resetResult.ExitCode != 0)
        {
            return new GitResetResult(false, "Git reset failed", resetResult.StandardError, false);
        }

        return new GitResetResult(true, "File reset successfully", null, false);
    }

    public async Task<GitIndexResult> SetStagedAsync(
        string path,
        bool staged,
        CancellationToken cancellationToken = default
    )
    {
        var gitDirectory = FindGitDirectory(path);
        if (gitDirectory == null)
        {
            return new GitIndexResult(false, "Path is not in a git repository", null, true);
        }

        var indexMutationLock = IndexMutationLocks.GetOrAdd(
            Path.GetFullPath(gitDirectory),
            static _ => new SemaphoreSlim(1, 1)
        );
        await indexMutationLock.WaitAsync(cancellationToken);
        try
        {
            var relativePath = Path.GetRelativePath(gitDirectory, path).Replace("\\", "/");
            var statusResult = await RunGitAsync(
                gitDirectory,
                ["status", "--porcelain", "--", relativePath],
                cancellationToken
            );
            if (statusResult.ExitCode != 0)
            {
                return new GitIndexResult(false, "Failed to check git status", statusResult.StandardError, false);
            }

            if (string.IsNullOrWhiteSpace(statusResult.StandardOutput))
            {
                return new GitIndexResult(
                    false,
                    staged ? "Path has no changes to stage" : "Path has no staged changes to unstage",
                    null,
                    true
                );
            }

            if (!staged && !HasStagedChanges(statusResult.StandardOutput))
            {
                return new GitIndexResult(false, "Path has no staged changes to unstage", null, true);
            }

            IReadOnlyCollection<string> arguments = staged
                ? ["add", "--", relativePath]
                : ["restore", "--staged", "--", relativePath];
            var result = await RunGitAsync(gitDirectory, arguments, cancellationToken);
            if (result.ExitCode != 0)
            {
                return new GitIndexResult(
                    false,
                    staged ? "Failed to stage changes" : "Failed to unstage changes",
                    result.StandardError,
                    IsPathspecError(result.StandardError)
                );
            }

            return new GitIndexResult(
                true,
                staged ? "Changes staged successfully" : "Changes unstaged successfully",
                null,
                false
            );
        }
        finally
        {
            indexMutationLock.Release();
        }
    }

    private static bool HasStagedChanges(string porcelainOutput)
    {
        return porcelainOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Length >= 2 && line[0] is not ' ' and not '?');
    }

    private static bool IsPathspecError(string standardError)
    {
        return standardError.Contains("pathspec", StringComparison.OrdinalIgnoreCase)
            && standardError.Contains("did not match", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<GitCloneResult> CloneRepositoryAsync(
        string gitAddress,
        string workingDirectory,
        CancellationToken cancellationToken = default
    )
    {
        var dirFullInfo = PathUtil.ExpandTilde(workingDirectory);
        if (!Directory.Exists(dirFullInfo))
        {
            Directory.CreateDirectory(dirFullInfo);
        }

        var result = await RunGitAsync(dirFullInfo, ["clone", gitAddress, "."], cancellationToken);
        if (result.ExitCode != 0)
        {
            return new GitCloneResult(false, result.StandardError, result.StandardOutput, result.StandardError);
        }

        return new GitCloneResult(true, null, result.StandardOutput, result.StandardError);
    }

    private static async Task<BufferedCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken
    )
    {
        return await Cli.Wrap("git")
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
    }

    private static string? FindGitDirectory(string filePath)
    {
        string? directory = Directory.Exists(filePath) ? filePath : Path.GetDirectoryName(filePath);

        while (directory != null)
        {
            var gitPath = Path.Combine(directory, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }
}
