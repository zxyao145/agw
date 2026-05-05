using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Files;

public class FileReadToolParams
{
    [Description(
        """
        The absolute path to the file to read.
        """
    )]
    public string FilePath { get; set; } = "";

    [Description(
        """
        The line number to start reading from. Only provide if the file is too large to read at once.
        """
    )]
    public int? Offset { get; set; }

    [Description(
        """
        The number of lines to read. Only provide if the file is too large to read at once.
        """
    )]
    public int? Limit { get; set; }

    [Description(
        """
        Page range for PDF files (e.g., "1-5", "3", "10-20"). Only applicable to PDF files.
        """
    )]
    public string? Pages { get; set; }
}

public class FileReadToolResult
{
    public string FilePath { get; set; } = "";
    public string Content { get; set; } = "";
    public int NumLines { get; set; }
    public int StartLine { get; set; }
    public int TotalLines { get; set; }
}

internal class FileReadTool : IAgwTool
{
    public string Name => "read_file";

    public string Category => "File";

    [Description(
        """
        Reads a file from the local filesystem. You can access any file directly by using this tool.
        Supports reading specific line ranges via offset and limit parameters.
        """
    )]
    public FileReadToolResult Execute(FileReadToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.FilePath))
        {
            throw new AgwException(ErrorCodes.FilePathRequired);
        }

        var fullPath = Path.GetFullPath(toolParams.FilePath);

        if (!File.Exists(fullPath))
        {
            throw new AgwException(ErrorCodes.FileNotFound, $"File '{fullPath}' does not exist.");
        }

        var allLines = File.ReadAllLines(fullPath);
        var totalLines = allLines.Length;

        var startLine = toolParams.Offset ?? 1;
        if (startLine < 1) startLine = 1;
        if (startLine > totalLines && totalLines > 0)
        {
            throw new AgwException(ErrorCodes.OffsetOutOfRange, $"The file exists but is shorter than the provided offset ({startLine}). The file has {totalLines} lines.");
        }

        var lineOffset = startLine - 1;
        var limit = toolParams.Limit;

        string[] selectedLines;
        if (limit.HasValue)
        {
            selectedLines = allLines.Skip(lineOffset).Take(limit.Value).ToArray();
        }
        else
        {
            selectedLines = allLines.Skip(lineOffset).ToArray();
        }

        var content = string.Join("\n", selectedLines);

        return new FileReadToolResult
        {
            FilePath = toolParams.FilePath,
            Content = content,
            NumLines = selectedLines.Length,
            StartLine = startLine,
            TotalLines = totalLines
        };
    }

    public AITool ToAITool()
    {
        Func<FileReadToolParams, FileReadToolResult> func = Execute;
        var aiTool = AIFunctionFactory.Create(func, Name);
        return aiTool;
    }
}
