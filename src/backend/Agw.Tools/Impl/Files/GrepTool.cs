using System.Text.RegularExpressions;

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
        File or directory to search in. Defaults to current working directory.
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

internal class GrepTool : IAgwTool
{
    public string Name => "grep";

    public string Category => "File";

    private const int DefaultHeadLimit = 250;

    [Description(
        """
        A powerful search tool built on regular expressions.
        Usage: search file contents with regex.
        """
    )]
    public GrepToolResult Execute(GrepToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.Pattern))
        {
            throw new AgwException(ErrorCodes.PatternRequired, "Pattern is required.");
        }

        var searchPath = string.IsNullOrWhiteSpace(toolParams.Path)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(toolParams.Path);

        if (!Directory.Exists(searchPath) && !File.Exists(searchPath))
        {
            throw new AgwException(ErrorCodes.DirectoryNotFound, $"Path '{searchPath}' does not exist.");
        }

        var regexOptions = RegexOptions.Compiled;
        if (toolParams.CaseInsensitive)
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }
        if (toolParams.Multiline)
        {
            regexOptions |= RegexOptions.Singleline;
        }

        Regex regex;
        try
        {
            regex = new Regex(toolParams.Pattern, regexOptions);
        }
        catch (ArgumentException ex)
        {
            throw new AgwException(ErrorCodes.InvalidPattern, $"Invalid regex pattern: {ex.Message}");
        }

        // Determine file filter
        var extensions = GetExtensionsFromType(toolParams.Type);
        var globPattern = toolParams.Glob;

        var filesToSearch = GetFilesToSearch(searchPath, extensions, globPattern);

        var outputMode = toolParams.OutputMode.ToLowerInvariant() switch
        {
            "content" => "content",
            "count" => "count",
            _ => "files_with_matches"
        };

        if (outputMode == "content")
        {
            return SearchContentMode(filesToSearch, regex, toolParams);
        }
        else if (outputMode == "count")
        {
            return SearchCountMode(filesToSearch, regex, toolParams);
        }
        else
        {
            return SearchFilesMode(filesToSearch, regex, toolParams);
        }
    }

    public AITool ToAITool()
    {
        Func<GrepToolParams, GrepToolResult> func = Execute;
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }

    private static List<string> GetFilesToSearch(string searchPath, List<string>? extensions, string? globPattern)
    {
        var files = new List<string>();

        if (File.Exists(searchPath))
        {
            files.Add(searchPath);
            return files;
        }

        var searchOption = SearchOption.AllDirectories;
        var allFiles = Directory.GetFiles(searchPath, "*", searchOption);

        foreach (var file in allFiles)
        {
            // Skip VCS directories
            if (file.Contains("\\.git\\") || file.Contains("/.git/"))
                continue;

            // Filter by extension
            if (extensions != null && extensions.Count > 0)
            {
                var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
                if (!extensions.Contains(ext))
                    continue;
            }

            // Filter by glob
            if (!string.IsNullOrEmpty(globPattern))
            {
                var filename = Path.GetFileName(file);
                if (!MatchesGlob(filename, globPattern))
                    continue;
            }

            files.Add(file);
        }

        return files;
    }

    private static List<string>? GetExtensionsFromType(string? type)
    {
        if (string.IsNullOrEmpty(type))
            return null;

        return type.ToLowerInvariant() switch
        {
            "js" => new List<string> { "js", "jsx", "mjs", "cjs" },
            "ts" => new List<string> { "ts", "tsx", "mts", "cts" },
            "py" => new List<string> { "py", "pyw", "pyi" },
            "rust" => new List<string> { "rs" },
            "go" => new List<string> { "go" },
            "java" => new List<string> { "java" },
            "cs" => new List<string> { "cs" },
            "cpp" => new List<string> { "cpp", "cc", "cxx", "h", "hpp" },
            "c" => new List<string> { "c", "h" },
            "rb" => new List<string> { "rb" },
            "php" => new List<string> { "php" },
            _ => new List<string> { type }
        };
    }

    private static bool MatchesGlob(string filename, string pattern)
    {
        // Simple glob matching
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

    private static GrepToolResult SearchFilesMode(List<string> files, Regex regex, GrepToolParams toolParams)
    {
        var matchingFiles = new List<string>();

        foreach (var file in files)
        {
            try
            {
                var content = File.ReadAllText(file);
                if (regex.IsMatch(content))
                {
                    matchingFiles.Add(file);
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        // Sort by modification time
        matchingFiles = matchingFiles
            .Select(f => new { File = f, Mtime = File.GetLastWriteTimeUtc(f) })
            .OrderByDescending(x => x.Mtime)
            .ThenBy(x => x.File)
            .Select(x => x.File)
            .ToList();

        var (limitedItems, appliedLimit) = ApplyHeadLimit(matchingFiles, toolParams.HeadLimit, toolParams.Offset);

        // Convert to relative paths
        var cwd = Directory.GetCurrentDirectory();
        var relativeFiles = limitedItems
            .Select(f => f.StartsWith(cwd) ? f[cwd.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : f)
            .ToList();

        return new GrepToolResult
        {
            Mode = "files_with_matches",
            NumFiles = relativeFiles.Count,
            Filenames = relativeFiles,
            AppliedLimit = appliedLimit,
            AppliedOffset = toolParams.Offset > 0 ? toolParams.Offset : null
        };
    }

    private static GrepToolResult SearchContentMode(List<string> files, Regex regex, GrepToolParams toolParams)
    {
        var contextLines = toolParams.Context ?? toolParams.ContextBefore ?? toolParams.ContextAfter ?? 0;
        var beforeLines = toolParams.ContextBefore ?? contextLines;
        var afterLines = toolParams.ContextAfter ?? contextLines;

        var allLines = new List<string>();

        foreach (var file in files)
        {
            try
            {
                var lines = File.ReadAllLines(file);
                var fileName = Path.GetFileName(file);

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

    private static GrepToolResult SearchCountMode(List<string> files, Regex regex, GrepToolParams toolParams)
    {
        var countLines = new List<string>();
        int totalMatches = 0;
        int fileCount = 0;

        foreach (var file in files)
        {
            try
            {
                var content = File.ReadAllText(file);
                var matches = regex.Matches(content).Count;
                if (matches > 0)
                {
                    var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
                    countLines.Add($"{relativePath}:{matches}");
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
