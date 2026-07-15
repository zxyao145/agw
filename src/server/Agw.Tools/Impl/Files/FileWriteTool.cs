using Agw.Files.Abstracts;
using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Files;

public class FileWriteToolParams
{
    [Description(
        """
        The path to the file to write, relative to the project workspace root.
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

internal class FileWriteTool : IProjectScopedAgwTool
{
    private readonly IAgwFileSystemResolver _resolver;

    public FileWriteTool(IAgwFileSystemResolver resolver) => _resolver = resolver;

    public string Name => "write_file";

    public string Category => "File";

    [Description(
        """
        Write a file to the project workspace. Creates the file if it does not exist,
        or overwrites it if it does.
        """
    )]
    public async Task<FileWriteToolResult> ExecuteAsync(
        FileWriteToolParams toolParams, Guid projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.FilePath))
        {
            throw new AgwException(ErrorCodes.FilePathRequired);
        }

        var fs = await _resolver.ResolveAsync(projectId, ct);
        bool isUpdate = await fs.ExistsFileAsync(toolParams.FilePath, ct);
        string? originalContent = isUpdate ? await fs.ReadAllTextAsync(toolParams.FilePath, ct) : null;

        await fs.WriteAllTextAsync(toolParams.FilePath, toolParams.Content, ct);

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

    public AITool ToAITool() => ToAITool(Guid.Empty);

    public AITool ToAITool(Guid projectId)
    {
        Func<FileWriteToolParams, CancellationToken, Task<FileWriteToolResult>> func =
            (p, ct) => ExecuteAsync(p, projectId, ct);
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
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
