using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
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
        The directory to search in, relative to the project workspace root. If not specified, the workspace root will be used.
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

internal class GlobTool : IProjectScopedAgwTool
{
    private readonly IAgwFileSystemResolver _resolver;

    public GlobTool(IAgwFileSystemResolver resolver) => _resolver = resolver;

    public string Name => "glob";

    public string Category => "File";

    [Description(
        """
        Fast file pattern matching tool that works with any codebase size.
        Supports glob patterns like "**/*.js" or "src/**/*.ts".
        Returns matching file paths sorted by modification time.
        """
    )]
    public async Task<GlobToolResult> ExecuteAsync(
        GlobToolParams toolParams, Guid projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.Pattern))
        {
            throw new AgwException(ErrorCodes.PatternRequired, "Pattern is required.");
        }

        var fs = await _resolver.ResolveAsync(projectId, ct);
        var searchPath = string.IsNullOrWhiteSpace(toolParams.Path) ? "." : toolParams.Path;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var pattern = toolParams.Pattern;

        bool recursive = pattern.Contains("**");
        var cleanPattern = pattern.Replace("**/", "").Replace("**", "*");

        var files = new List<FileEntry>();
        await foreach (var entry in fs.EnumerateAsync(searchPath, cleanPattern, recursive, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.IsDirectory)
            {
                files.Add(entry);
            }
        }

        const int maxResults = 100;
        bool truncated = files.Count > maxResults;
        var limitedFiles = files
            .OrderByDescending(f => f.LastModifiedUtc)
            .Take(maxResults)
            .Select(f => f.Path)
            .ToList();

        stopwatch.Stop();

        return new GlobToolResult
        {
            DurationMs = stopwatch.ElapsedMilliseconds,
            NumFiles = limitedFiles.Count,
            Filenames = limitedFiles,
            Truncated = truncated
        };
    }

    public AITool ToAITool() => ToAITool(Guid.Empty);

    public AITool ToAITool(Guid projectId)
    {
        Func<GlobToolParams, CancellationToken, Task<GlobToolResult>> func =
            (p, ct) => ExecuteAsync(p, projectId, ct);
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }
}
