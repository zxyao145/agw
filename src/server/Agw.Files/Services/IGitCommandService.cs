namespace Agw.Files.Services;

public interface IGitCommandService
{
    Task<GitChangedFiles?> GetChangedFilesAsync(string directory, CancellationToken cancellationToken = default);

    Task<GitDiffResult> GetDiffAsync(
        string filePath,
        CancellationToken cancellationToken = default,
        GitDiffScope scope = GitDiffScope.All
    );

    Task<GitResetResult> ResetFileAsync(string filePath, CancellationToken cancellationToken = default);

    Task<GitIndexResult> SetStagedAsync(string path, bool staged, CancellationToken cancellationToken = default);

    Task<GitCloneResult> CloneRepositoryAsync(
        string gitAddress,
        string workingDirectory,
        CancellationToken cancellationToken = default
    );
}
