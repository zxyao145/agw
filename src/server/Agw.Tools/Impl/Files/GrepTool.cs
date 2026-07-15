using System.Text.RegularExpressions;

using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Files;

public class GrepToolParams
{
    [Description(
        """
        The regular expression pattern to search for in file contents.
        """
    )]
    public string Pattern { get; set; } = "";

    [Description(
        """
        File or directory to search in, relative to the project workspace root. Defaults to workspace root.
        """
    )]
    public string? Path { get; set; }

    [Description(
        """
        Glob pattern to filter files (e.g. "*.js", "*.{ts,tsx}") - maps to rg --glob.
        """
    )]
    public string? Glob { get; set; }

    [Description(
        """
        Output mode: "content" shows matching lines, "files_with_matches" shows file paths,
        "count" shows match counts. Defaults to "files_with_matches".
        """
    )]
    public string OutputMode { get; set; } = "files_with_matches";

    [Description(
        """
        Number of lines to show before each match. Requires output_mode: "content", ignored otherwise.
        """
    )]
    public int? ContextBefore { get; set; }

    [Description(
        """
        Number of lines to show after each match. Requires output_mode: "content", ignored otherwise.
        """
    )]
    public int? ContextAfter { get; set; }

    [Description(
        """
        Alias for context. Number of lines to show before and after each match.
        """
    )]
    public int? Context { get; set; }

    [Description(
        """
        Show line numbers in output. Requires output_mode: "content", ignored otherwise. Defaults to true.
        """
    )]
    public bool ShowLineNumbers { get; set; } = true;

    [Description(
        """
        Case insensitive search.
        """
    )]
    public bool CaseInsensitive { get; set; }

    [Description(
        """
        File type to search. Common types: js, py, rust, go, java, etc.
        """
    )]
    public string? Type { get; set; }

    [Description(
        """
        Limit output to first N lines/entries. Defaults to 250 when unspecified. Pass 0 for unlimited.
        """
    )]
    public int? HeadLimit { get; set; }

    [Description(
        """
        Skip first N lines/entries before applying head_limit. Defaults to 0.
        """
    )]
    public int Offset { get; set; }

    [Description(
        """
        Enable multiline mode where . matches newlines and patterns can span lines. Default: false.
        """
    )]
    public bool Multiline { get; set; }
}

public class GrepToolResult
{
    public string? Mode { get; set; }
    public int NumFiles { get; set; }
    public List<string> Filenames { get; set; } = new();
    public string? Content { get; set; }
    public int? NumLines { get; set; }
    public int? NumMatches { get; set; }
    public int? AppliedLimit { get; set; }
    public int? AppliedOffset { get; set; }
}

internal class GrepTool : IProjectScopedAgwTool
{
    private readonly IAgwFileSystemResolver _resolver;

    public GrepTool(IAgwFileSystemResolver resolver) => _resolver = resolver;

    public string Name => "grep";

    public string Category => "File";

    private const int DefaultHeadLimit = 250;

    [Description(
        """
        A powerful search tool built on regular expressions.
        Usage: search file contents with regex.
        """
    )]
    public async Task<GrepToolResult> ExecuteAsync(
        GrepToolParams toolParams, Guid projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.Pattern))
        {
            throw new AgwException(ErrorCodes.PatternRequired, "Pattern is required.");
        }

        var fs = await _resolver.ResolveAsync(projectId, ct);
        var searchPath = string.IsNullOrWhiteSpace(toolParams.Path) ? "." : toolParams.Path;

        var searchOptions = new SearchOptions(
            Pattern: toolParams.Pattern,
            IsRegex: true,
            CaseInsensitive: toolParams.CaseInsensitive,
            Multiline: toolParams.Multiline,
            IncludeExtensions: GetExtensionsFromType(toolParams.Type),
            FilenameGlob: toolParams.Glob,
            MaxHits: null);

        var outputMode = toolParams.OutputMode.ToLowerInvariant() switch
        {
            "content" => "content",
            "count" => "count",
            _ => "files_with_matches"
        };

        if (outputMode == "content")
        {
            return await SearchContentMode(fs, searchPath, searchOptions, toolParams, ct);
        }
        else if (outputMode == "count")
        {
            return await SearchCountMode(fs, searchPath, searchOptions, toolParams, ct);
        }
        else
        {
            return await SearchFilesMode(fs, searchPath, searchOptions, toolParams, ct);
        }
    }

    public AITool ToAITool() => ToAITool(Guid.Empty);

    public AITool ToAITool(Guid projectId)
    {
        Func<GrepToolParams, CancellationToken, Task<GrepToolResult>> func =
            (p, ct) => ExecuteAsync(p, projectId, ct);
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }

    private async Task<GrepToolResult> SearchFilesMode(
        IAgwFileSystem fs, string searchPath, SearchOptions options, GrepToolParams toolParams, CancellationToken ct)
    {
        var matchingFiles = new List<(string Path, DateTimeOffset Mtime)>();
        var regex = BuildRegex(toolParams.Pattern, toolParams.CaseInsensitive, toolParams.Multiline);

        await foreach (var entry in fs.EnumerateAsync(searchPath, "*", true, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (entry.IsDirectory) continue;
            if (!MatchesFilters(entry.Path, toolParams.Type, toolParams.Glob)) continue;

            try
            {
                var content = await fs.ReadAllTextAsync(entry.Path, ct);
                if (regex.IsMatch(content))
                {
                    matchingFiles.Add((entry.Path, entry.LastModifiedUtc));
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        matchingFiles = matchingFiles
            .OrderByDescending(x => x.Mtime)
            .ThenBy(x => x.Path)
            .ToList();

        var (limitedItems, appliedLimit) = ApplyHeadLimit(
            matchingFiles.Select(x => x.Path).ToList(), toolParams.HeadLimit, toolParams.Offset);

        return new GrepToolResult
        {
            Mode = "files_with_matches",
            NumFiles = limitedItems.Count,
            Filenames = limitedItems,
            AppliedLimit = appliedLimit,
            AppliedOffset = toolParams.Offset > 0 ? toolParams.Offset : null
        };
    }

    private async Task<GrepToolResult> SearchContentMode(
        IAgwFileSystem fs, string searchPath, SearchOptions options, GrepToolParams toolParams, CancellationToken ct)
    {
        var contextLines = toolParams.Context ?? toolParams.ContextBefore ?? toolParams.ContextAfter ?? 0;
        var beforeLines = toolParams.ContextBefore ?? contextLines;
        var afterLines = toolParams.ContextAfter ?? contextLines;
        var regex = BuildRegex(toolParams.Pattern, toolParams.CaseInsensitive, toolParams.Multiline);

        var allLines = new List<string>();

        await foreach (var entry in fs.EnumerateAsync(searchPath, "*", true, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (entry.IsDirectory) continue;
            if (!MatchesFilters(entry.Path, toolParams.Type, toolParams.Glob)) continue;

            try
            {
                var lines = await fs.ReadAllLinesAsync(entry.Path, ct);
                var fileName = System.IO.Path.GetFileName(entry.Path);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        var startIdx = Math.Max(0, i - beforeLines);
                        var endIdx = Math.Min(lines.Length - 1, i + afterLines);

                        for (int j = startIdx; j <= endIdx; j++)
                        {
                            var prefix = toolParams.ShowLineNumbers ? $"{fileName}:{j + 1}:" : $"{fileName}:";
                            var line = prefix + lines[j];
                            if (!allLines.Contains(line))
                            {
                                allLines.Add(line);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        var (limitedItems, appliedLimit) = ApplyHeadLimit(allLines, toolParams.HeadLimit, toolParams.Offset);

        return new GrepToolResult
        {
            Mode = "content",
            NumFiles = 0,
            Content = string.Join("\n", limitedItems),
            NumLines = limitedItems.Count,
            AppliedLimit = appliedLimit,
            AppliedOffset = toolParams.Offset > 0 ? toolParams.Offset : null
        };
    }

    private async Task<GrepToolResult> SearchCountMode(
        IAgwFileSystem fs, string searchPath, SearchOptions options, GrepToolParams toolParams, CancellationToken ct)
    {
        var countLines = new List<string>();
        int totalMatches = 0;
        int fileCount = 0;
        var regex = BuildRegex(toolParams.Pattern, toolParams.CaseInsensitive, toolParams.Multiline);

        await foreach (var entry in fs.EnumerateAsync(searchPath, "*", true, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (entry.IsDirectory) continue;
            if (!MatchesFilters(entry.Path, toolParams.Type, toolParams.Glob)) continue;

            try
            {
                var content = await fs.ReadAllTextAsync(entry.Path, ct);
                var matches = regex.Matches(content).Count;
                if (matches > 0)
                {
                    countLines.Add($"{entry.Path}:{matches}");
                    totalMatches += matches;
                    fileCount++;
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        var (limitedItems, appliedLimit) = ApplyHeadLimit(countLines, toolParams.HeadLimit, toolParams.Offset);

        return new GrepToolResult
        {
            Mode = "count",
            NumFiles = fileCount,
            Content = string.Join("\n", limitedItems),
            NumMatches = totalMatches,
            AppliedLimit = appliedLimit,
            AppliedOffset = toolParams.Offset > 0 ? toolParams.Offset : null
        };
    }

    private static Regex BuildRegex(string pattern, bool caseInsensitive, bool multiline)
    {
        var options = RegexOptions.Compiled;
        if (caseInsensitive) options |= RegexOptions.IgnoreCase;
        if (multiline) options |= RegexOptions.Singleline;

        try
        {
            return new Regex(pattern, options);
        }
        catch (ArgumentException ex)
        {
            throw new AgwException(ErrorCodes.InvalidPattern, $"Invalid regex pattern: {ex.Message}");
        }
    }

    private static bool MatchesFilters(string path, string? type, string? globPattern)
    {
        if (!string.IsNullOrEmpty(type))
        {
            var extensions = GetExtensionsFromType(type);
            if (extensions is { Count: > 0 })
            {
                var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                if (!extensions.Contains(ext)) return false;
            }
        }

        if (!string.IsNullOrEmpty(globPattern))
        {
            var filename = System.IO.Path.GetFileName(path);
            if (!MatchesSimpleGlob(filename, globPattern)) return false;
        }

        return true;
    }

    private static List<string>? GetExtensionsFromType(string? type)
    {
        if (string.IsNullOrEmpty(type))
            return null;

        return type.ToLowerInvariant() switch
        {
            "js" => ["js", "jsx", "mjs", "cjs"],
            "ts" => ["ts", "tsx", "mts", "cts"],
            "py" => ["py", "pyw", "pyi"],
            "rust" => ["rs"],
            "go" => ["go"],
            "java" => ["java"],
            "cs" => ["cs"],
            "cpp" => ["cpp", "cc", "cxx", "h", "hpp"],
            "c" => ["c", "h"],
            "rb" => ["rb"],
            "php" => ["php"],
            _ => [type]
        };
    }

    private static bool MatchesSimpleGlob(string filename, string pattern)
    {
        var regexPattern = "^" + pattern
            .Replace(".", "\\.")
            .Replace("*", ".*")
            .Replace("?", ".")
            + "$";

        try
        {
            return Regex.IsMatch(filename, regexPattern, RegexOptions.IgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static (List<T> Items, int? AppliedLimit) ApplyHeadLimit<T>(List<T> items, int? headLimit, int offset)
    {
        if (headLimit == 0)
        {
            return (items.Skip(offset).ToList(), null);
        }

        var effectiveLimit = headLimit ?? DefaultHeadLimit;
        var sliced = items.Skip(offset).Take(effectiveLimit).ToList();
        var wasTruncated = items.Count - offset > effectiveLimit;

        return (sliced, wasTruncated ? effectiveLimit : null);
    }
}
