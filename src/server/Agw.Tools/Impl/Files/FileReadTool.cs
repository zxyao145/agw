using Agw.Shared.Contracts.Storage;
using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Files;

public class FileReadToolParams
{
    [Description(
        """
        The path to the file to read, relative to the project workspace root.
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
}

public class FileReadToolResult
{
    public string FilePath { get; set; } = "";
    public string Content { get; set; } = "";
    public int NumLines { get; set; }
    public int StartLine { get; set; }
    public int TotalLines { get; set; }
}

internal class FileReadTool : IProjectScopedAgwTool
{
    private readonly IAgwFileSystemResolver _resolver;

    public FileReadTool(IAgwFileSystemResolver resolver) => _resolver = resolver;

    public string Name => "read_file";

    public string Category => "File";

    [Description(
        """
        Reads a file from the project workspace. Supports reading specific line ranges via offset and limit parameters.
        """
    )]
    public async Task<FileReadToolResult> ExecuteAsync(
        FileReadToolParams toolParams, Guid projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.FilePath))
        {
            throw new AgwException(ErrorCodes.FilePathRequired);
        }

        var fs = await _resolver.ResolveAsync(projectId, ct);
        if (!await fs.ExistsFileAsync(toolParams.FilePath, ct))
        {
            throw new AgwException(ErrorCodes.FileNotFound, $"File '{toolParams.FilePath}' does not exist.");
        }

        var allLines = await fs.ReadAllLinesAsync(toolParams.FilePath, ct);
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

    public AITool ToAITool() => ToAITool(Guid.Empty);

    public AITool ToAITool(Guid projectId)
    {
        Func<FileReadToolParams, CancellationToken, Task<FileReadToolResult>> func =
            (p, ct) => ExecuteAsync(p, projectId, ct);
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }
}
