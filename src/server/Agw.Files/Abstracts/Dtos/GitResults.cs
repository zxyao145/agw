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

public record GitDiffResult(bool Success, string Diff, bool Unchanged, string? OriginalContent, string? Error);

public record GitResetResult(bool Success, string Message, string? Error, bool IsClientError);

public record GitIndexResult(bool Success, string Message, string? Error, bool IsClientError);

public record GitCloneResult(bool Success, string? Error, string? Stdout, string? Stderr);
