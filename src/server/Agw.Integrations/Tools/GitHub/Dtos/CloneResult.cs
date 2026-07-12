namespace Agw.Integrations.Tools.GitHub.Dtos;

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
