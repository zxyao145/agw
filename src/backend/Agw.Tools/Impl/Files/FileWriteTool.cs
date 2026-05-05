using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Files;

public class FileWriteToolParams
{
    [Description(
        """
        The absolute path to the file to write (must be absolute, not relative).
        """
    )]
    public string FilePath { get; set; } = "";

    [Description(
        """
        The content to write to the file.
        """
    )]
    public string Content { get; set; } = "";
}

public class FileWriteToolResult
{
    public string Type { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Content { get; set; } = "";
    public List<PatchHunk> StructuredPatch { get; set; } = new();
    public string? OriginalFile { get; set; }
}

internal class FileWriteTool : IAgwTool
{
    public string Name => "write_file";

    public string Category => "File";

    [Description(
        """
        Write a file to the local filesystem. Creates the file if it does not exist,
        or overwrites it if it does.
        """
    )]
    public FileWriteToolResult Execute(FileWriteToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.FilePath))
        {
            throw new AgwException(ErrorCodes.FilePathRequired);
        }

        var fullPath = Path.GetFullPath(toolParams.FilePath);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bool isUpdate = File.Exists(fullPath);
        string? originalContent = isUpdate ? File.ReadAllText(fullPath) : null;

        File.WriteAllText(fullPath, toolParams.Content);

        var result = new FileWriteToolResult
        {
            Type = isUpdate ? "update" : "create",
            FilePath = toolParams.FilePath,
            Content = toolParams.Content,
            OriginalFile = originalContent
        };

        if (isUpdate && originalContent != null)
        {
            result.StructuredPatch = GeneratePatch(originalContent, toolParams.Content);
        }

        return result;
    }

    public AITool ToAITool()
    {
        Func<FileWriteToolParams, FileWriteToolResult> func = Execute;
        var aiTool = AIFunctionFactory.Create(func, Name);
        return aiTool;
    }

    private static List<PatchHunk> GeneratePatch(string oldContent, string newContent)
    {
        var patch = new List<PatchHunk>();
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');

        var diffLines = new List<string>();
        diffLines.AddRange(oldLines.Select(l => "-" + l));
        diffLines.AddRange(newLines.Select(l => "+" + l));

        patch.Add(new PatchHunk
        {
            OldStart = 1,
            OldLines = oldLines.Length,
            NewStart = 1,
            NewLines = newLines.Length,
            Lines = diffLines
        });

        return patch;
    }
}
