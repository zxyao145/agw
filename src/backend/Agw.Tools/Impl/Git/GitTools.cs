using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

using Agw.Shared.Services;
using Agw.Shared.Utils;
using Agw.Tools.Attributes;

namespace Agw.Tools.Impl.Basic;


public class CloneResult
{
    public CloneResult(bool success, string? error, string? stdout, string? stderr)
    {
        Success = success;
        Error = error;
        Stdout = stdout;
        Stderr = stderr;
    }

    [Description("Indicates whether the git clone operation was successful.")]
    public bool Success { get; set; }

    [Description("Error message if the git clone operation failed.")]
    public string? Error { get; set; }

    [Description("Standard output from the git clone operation.")]
    public string? Stdout { get; set; }

    [Description("Standard error from the git clone operation.")]
    public string? Stderr { get; set; }
}



/// <summary>
/// Provides basic utility tools for agents.
/// </summary>
[AiToolContainer(DefaultCategory = "Git")]
public static class GitTools
{
    [AiTool("git_clone")]
    [Description("clone a git repository")]
    public static async Task<CloneResult> Clone
        (
       [NotNull, Description("remote git repository address")] string gitAddress,
       [NotNull, Description("local workspace path")] string workspace,
        CancellationToken cancellationToken = default
        )
    {
        var gitCommand = IocUtil.GetSingletonRequiredService<IGitCommandService>();
        var result = await gitCommand.CloneRepositoryAsync(gitAddress, workspace, cancellationToken);

        return new CloneResult(
            success: result.Success,
            error: result.Error,
            stdout: result.Stdout,
            stderr: result.Stderr
        );

    }
}
