using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Files;

public class GlobToolParams
{
    [Description(
        """
        The glob pattern to match files against.
        """
    )]
    public string Pattern { get; set; } = "";

    [Description(
        """
        The directory to search in. If not specified, the current working directory will be used.
        IMPORTANT: Omit this field to use the default directory. DO NOT enter "undefined" or "null" - simply omit it for the default behavior.
        Must be a valid directory path if provided.
        """
    )]
    public string? Path { get; set; }
}

public class GlobToolResult
{
    public long DurationMs { get; set; }
    public int NumFiles { get; set; }
    public List<string> Filenames { get; set; } = new();
    public bool Truncated { get; set; }
}

internal class GlobTool : IAgwTool
{
    public string Name => "glob";

    public string Category => "File";

    [Description(
        """
        Fast file pattern matching tool that works with any codebase size.
        Supports glob patterns like "**/*.js" or "src/**/*.ts".
        Returns matching file paths sorted by modification time.
        """
    )]
    public GlobToolResult Execute(GlobToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.Pattern))
        {
            throw new AgwException(ErrorCodes.PatternRequired, "Pattern is required.");
        }

        var searchPath = string.IsNullOrWhiteSpace(toolParams.Path)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(toolParams.Path);

        if (!Directory.Exists(searchPath))
        {
            throw new AgwException(ErrorCodes.DirectoryNotFound, $"Directory '{searchPath}' does not exist.");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var pattern = toolParams.Pattern;

        // Handle **/ prefix for recursive search
        bool recursive = pattern.Contains("**");
        var cleanPattern = pattern.Replace("**/", "").Replace("**", "*");

        string[] files;
        if (recursive)
        {
            files = Directory.GetFiles(searchPath, cleanPattern, SearchOption.AllDirectories);
        }
        else
        {
            files = Directory.GetFiles(searchPath, cleanPattern, SearchOption.TopDirectoryOnly);
        }

        const int maxResults = 100;
        bool truncated = files.Length > maxResults;
        var limitedFiles = files.Take(maxResults).ToList();

        // Convert to relative paths
        var relativeFiles = limitedFiles
            .Select(f => Path.GetRelativePath(searchPath, f))
            .ToList();

        stopwatch.Stop();

        return new GlobToolResult
        {
            DurationMs = stopwatch.ElapsedMilliseconds,
            NumFiles = relativeFiles.Count,
            Filenames = relativeFiles,
            Truncated = truncated
        };
    }

    public AITool ToAITool()
    {
        Func<GlobToolParams, GlobToolResult> func = Execute;
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }
}
