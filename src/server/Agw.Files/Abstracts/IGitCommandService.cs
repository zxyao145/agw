using Agw.Files.Abstracts.Dtos;

namespace Agw.Files.Abstracts;

public interface IGitCommandService
{
    Task<GitChangedFiles?> GetChangedFilesAsync(string directory, CancellationToken cancellationToken = default);

    Task<GitDiffResult> GetDiffAsync(
        string filePath,
        CancellationToken cancellationToken = default,
        GitDiffScope scope = GitDiffScope.All
    );

    /// <summary>
    /// 读取文件在 HEAD 或暂存区中的文字；没有提交、文件未被跟踪或该位置没有这个文件时内容为 null。
    /// Reads the file's text in HEAD or the index; the content is null without commits, for untracked files, or when the
    /// file is absent there.
    /// </summary>
    Task<GitBlobResult> ReadBlobAsync(
        string filePath,
        GitBlobSource source,
        CancellationToken cancellationToken = default
    );

    Task<GitResetResult> ResetFileAsync(string filePath, CancellationToken cancellationToken = default);

    Task<GitIndexResult> SetStagedAsync(string path, bool staged, CancellationToken cancellationToken = default);

    Task<GitCloneResult> CloneRepositoryAsync(
        string gitAddress,
        string workingDirectory,
        CancellationToken cancellationToken = default
    );
}
