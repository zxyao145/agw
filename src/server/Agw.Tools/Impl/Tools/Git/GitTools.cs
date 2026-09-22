namespace Agw.Tools.Impl.Tools.Git;

/// <summary>
/// Provides basic utility tools for agents.
/// </summary>
[AgwToolContainer(AgwToolPermission.Write, DefaultCategory = "Git")]
public static class GitTools
{
    [AgwTool("git_clone", AgwToolPermission.Write)]
    [Description("clone a git repository")]
    public static async Task<CloneResult> Clone(
        [NotNull, Description("remote git repository address")] string gitAddress,
        [NotNull, Description("local workspace path")] string workspace,
        [AgwToolService] IGitCommandService gitCommand,
        CancellationToken cancellationToken = default
    )
    {
        var result = await gitCommand.CloneRepositoryAsync(gitAddress, workspace, cancellationToken);

        return new CloneResult(
            success: result.Success,
            error: result.Error,
            stdout: result.Stdout,
            stderr: result.Stderr
        );
    }
}
