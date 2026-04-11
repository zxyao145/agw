using Agw.Shared.Utils;

using CliWrap;
using CliWrap.Buffered;

using Microsoft.Extensions.Logging;

namespace Agw.Shared.Services;

public record GitChangedFiles(
    Dictionary<string, string> FileStatuses, // path -> status ("added", "modified", "deleted", "untracked")
    HashSet<string> DeletedFiles
);

public record GitDiffResult(
    bool Success,
    string Diff,
    bool Unchanged,
    string? OriginalContent,
    string? Error
);

public record GitResetResult(
    bool Success,
    string Message,
    string? Error,
    bool IsClientError
);

public record GitCloneResult(
    bool Success,
    string? Error,
    string? Stdout,
    string? Stderr
);

public class GitCommandService : IGitCommandService
{
    private readonly ILogger<GitCommandService> _logger;

    public GitCommandService(ILogger<GitCommandService> logger)
    {
        _logger = logger;
    }

    public async Task<GitChangedFiles?> GetChangedFilesAsync(string directory, CancellationToken cancellationToken = default)
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

            var fileStatuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var deletedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.Length < 3) continue;

                // Git status format: XY filename
                // X = index status, Y = working tree status
                var statusCode = line.Substring(0, 2);
                var filename = line.Substring(3).Trim().Trim('"');

                var fullPath = Path.GetFullPath(Path.Combine(gitDirectory, filename));

                // Determine git status
                string status;
                if (statusCode == "??")
                {
                    status = "untracked";
                }
                else if (statusCode.Contains('D'))
                {
                    status = "deleted";
                    deletedFiles.Add(fullPath);
                }
                else if (statusCode.Contains('A'))
                {
                    status = "added";
                }
                else
                {
                    status = "modified";
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

    public async Task<GitDiffResult> GetDiffAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var gitDirectory = FindGitDirectory(filePath);
        if (gitDirectory == null)
        {
            return new GitDiffResult(false, string.Empty, false, null, "File is not in a git repository");
        }

        var diffResult = await RunGitAsync(gitDirectory, ["diff", "HEAD", filePath], cancellationToken);
        if (diffResult.ExitCode != 0 && !string.IsNullOrEmpty(diffResult.StandardError))
        {
            return new GitDiffResult(false, string.Empty, false, null, diffResult.StandardError);
        }

        if (!string.IsNullOrWhiteSpace(diffResult.StandardOutput))
        {
            return new GitDiffResult(true, diffResult.StandardOutput, false, null, null);
        }

        var gitRootResult = await RunGitAsync(gitDirectory, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (gitRootResult.ExitCode != 0)
        {
            return new GitDiffResult(false, string.Empty, false, null, "Failed to get git root directory");
        }

        var gitRoot = gitRootResult.StandardOutput.Trim();
        var relativePath = Path.GetRelativePath(gitRoot, filePath).Replace("\\", "/");

        var showResult = await RunGitAsync(gitDirectory, ["show", $"HEAD:{relativePath}"], cancellationToken);
        if (showResult.ExitCode == 0)
        {
            return new GitDiffResult(true, string.Empty, true, showResult.StandardOutput, null);
        }

        return new GitDiffResult(true, string.Empty, false, null, null);
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

    public async Task<GitCloneResult> CloneRepositoryAsync(string gitAddress, string workingDirectory, CancellationToken cancellationToken = default)
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
        CancellationToken cancellationToken)
    {
        return await Cli.Wrap("git")
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
    }

    private static string? FindGitDirectory(string filePath)
    {
        string? directory;
        if (Directory.Exists(filePath))
        {
            directory = filePath;
        }
        else
        {
            directory = Path.GetDirectoryName(filePath);
        }

        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory, ".git")))
            {
                return directory;
            }
            directory = Directory.GetParent(directory)?.FullName;
        }
        return null;
    }
}
