using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Files;

public class FileEditToolParams
{
    [Description(
        """
        The absolute path to the file to modify.
        """
    )]
    public string FilePath { get; set; } = "";

    [Description(
        """
        The text to replace.
        """
    )]
    public string OldString { get; set; } = "";

    [Description(
        """
        The text to replace it with (must be different from old_string).
        """
    )]
    public string NewString { get; set; } = "";

    [Description(
        """
        Replace all occurrences of old_string (default false).
        """
    )]
    public bool ReplaceAll { get; set; } = false;
}

public class PatchHunk
{
    public int OldStart { get; set; }
    public int OldLines { get; set; }
    public int NewStart { get; set; }
    public int NewLines { get; set; }
    public List<string> Lines { get; set; } = new();
}

public class FileEditToolResult
{
    public string FilePath { get; set; } = "";
    public string OldString { get; set; } = "";
    public string NewString { get; set; } = "";
    public string OriginalFile { get; set; } = "";
    public List<PatchHunk> StructuredPatch { get; set; } = new();
    public bool UserModified { get; set; }
    public bool ReplaceAll { get; set; }
}

internal class FileEditTool : IAgwTool
{
    public string Name => "file_edit";

    public string Category => "File";

    [Description(
        """
        Performs an edit operation on a file. Replaces old_string with new_string.
        If replace_all is true, all occurrences are replaced.
        """
    )]
    public FileEditToolResult Execute(FileEditToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.FilePath))
        {
            throw new AgwException(ErrorCodes.FilePathRequired);
        }

        var fullPath = Path.GetFullPath(toolParams.FilePath);

        if (!File.Exists(fullPath) && !string.IsNullOrEmpty(toolParams.OldString))
        {
            throw new AgwException(ErrorCodes.FileNotFound, $"File '{fullPath}' does not exist.");
        }

        var originalContent = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";

        if (toolParams.OldString == toolParams.NewString)
        {
            throw new AgwException(ErrorCodes.NoChangesToMake, "No changes to make: old_string and new_string are exactly the same.");
        }

        var oldString = toolParams.OldString;
        var newString = toolParams.NewString;

        if (string.IsNullOrEmpty(oldString) && !string.IsNullOrEmpty(originalContent))
        {
            throw new AgwException(ErrorCodes.FileAlreadyExists, "Cannot create new file - file already exists.");
        }

        string updatedContent;
        if (toolParams.ReplaceAll)
        {
            updatedContent = originalContent.Replace(oldString, newString);
        }
        else
        {
            var matchCount = originalContent.Split(oldString).Length - 1;
            if (matchCount > 1)
            {
                throw new AgwException(ErrorCodes.MultipleMatches, $"Found {matchCount} matches of the string to replace, but replace_all is false.");
            }
            updatedContent = originalContent.Replace(oldString, newString, StringComparison.Ordinal);
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, updatedContent);

        var patch = new List<PatchHunk>();
        if (!string.IsNullOrEmpty(originalContent))
        {
            patch = GeneratePatch(originalContent, updatedContent, fullPath);
        }

        return new FileEditToolResult
        {
            FilePath = toolParams.FilePath,
            OldString = oldString,
            NewString = newString,
            OriginalFile = originalContent,
            StructuredPatch = patch,
            UserModified = false,
            ReplaceAll = toolParams.ReplaceAll
        };
    }

    public AITool ToAITool()
    {
        Func<FileEditToolParams, FileEditToolResult> func = Execute;
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }

    private static List<PatchHunk> GeneratePatch(string oldContent, string newContent, string filePath)
    {
        var patch = new List<PatchHunk>();
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');

        var diffLines = new List<string>();
        int oldStart = 1;
        int oldLinesCount = oldLines.Length;
        int newStart = 1;
        int newLinesCount = newLines.Length;

        diffLines.AddRange(oldLines.Select(l => "-" + l));
        diffLines.AddRange(newLines.Select(l => "+" + l));

        patch.Add(new PatchHunk
        {
            OldStart = oldStart,
            OldLines = oldLinesCount,
            NewStart = newStart,
            NewLines = newLinesCount,
            Lines = diffLines
        });

        return patch;
    }
}
