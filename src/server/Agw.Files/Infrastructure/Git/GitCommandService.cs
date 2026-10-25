using System.Collections.Concurrent;
using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Shared.Utils;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace Agw.Files.Infrastructure.Git;

public class GitCommandService : IGitCommandService
{
    private const string NotInRepository = "File is not in a git repository";

    /// <summary>
    /// `git rev-parse --verify --quiet` 在对象不存在时的退出码。The exit code of `git rev-parse --verify --quiet` for
    /// a missing object.
    /// </summary>
    private const int MissingObjectExitCode = 1;

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
            return new GitDiffResult(false, string.Empty, NotInRepository);
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
            return new GitDiffResult(false, string.Empty, diffResult.StandardError);
        }

        if (!string.IsNullOrWhiteSpace(diffResult.StandardOutput))
        {
            return new GitDiffResult(true, diffResult.StandardOutput, null);
        }

        if (scope == GitDiffScope.Unstaged)
        {
            var untrackedDiff = await GetUntrackedDiffAsync(gitDirectory, filePath, cancellationToken);
            if (untrackedDiff != null)
            {
                return untrackedDiff;
            }
        }

        return new GitDiffResult(true, string.Empty, null);
    }

    public async Task<GitBlobResult> ReadBlobAsync(
        string filePath,
        GitBlobSource source,
        CancellationToken cancellationToken = default
    )
    {
        var gitDirectory = FindGitDirectory(filePath);
        if (gitDirectory == null)
        {
            return new GitBlobResult(false, null, NotInRepository);
        }

        // 相对路径以 FindGitDirectory 的结果为基准，与 ResetFileAsync 相同。The relative path is based on
        // FindGitDirectory, as in ResetFileAsync.
        var relativePath = Path.GetRelativePath(gitDirectory, filePath).Replace("\\", "/");
        var objectName = source == GitBlobSource.Head ? $"HEAD:{relativePath}" : $":0:{relativePath}";
        var verifyResult = await RunGitAsync(
            gitDirectory,
            ["rev-parse", "--verify", "--quiet", objectName],
            cancellationToken
        );
        if (verifyResult.ExitCode == MissingObjectExitCode)
        {
            return new GitBlobResult(true, null, null);
        }

        if (verifyResult.ExitCode != 0)
        {
            return new GitBlobResult(false, null, verifyResult.StandardError);
        }

        var blobResult = await RunGitAsync(gitDirectory, ["cat-file", "blob", objectName], cancellationToken);
        return blobResult.ExitCode == 0
            ? new GitBlobResult(true, blobResult.StandardOutput, null)
            : new GitBlobResult(false, null, blobResult.StandardError);
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
                    return new GitDiffResult(true, string.Empty, null);
                }

                return new GitDiffResult(false, string.Empty, diffResult.StandardError);
            }

            return new GitDiffResult(true, diffResult.StandardOutput, null);
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

        var relativePath = Path.GetRelativePath(gitDirectory, filePath).Replace("\\", "/");
        var statusResult = await RunGitAsync(
            gitDirectory,
            ["status", "--porcelain", "--", relativePath],
            cancellationToken
        );
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
