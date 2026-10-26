namespace Agw.Files.Abstracts.Dtos;

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

public record GitDiffResult(bool Success, string Diff, string? Error);

/// <summary>
/// 读取文件内容的位置：HEAD 提交或暂存区（index）。Where a file's content is read: the HEAD commit or the index.
/// </summary>
public enum GitBlobSource
{
    Head,
    Index,
}

/// <summary>
/// 读取结果；该位置没有这个文件时 <see cref="Content"/> 为 null。The read result; <see cref="Content"/> is null when
/// the file is absent there.
/// </summary>
public record GitBlobResult(bool Success, string? Content, string? Error);

public record GitResetResult(bool Success, string Message, string? Error, bool IsClientError);

public record GitIndexResult(bool Success, string Message, string? Error, bool IsClientError);

public record GitCloneResult(bool Success, string? Error, string? Stdout, string? Stderr);
