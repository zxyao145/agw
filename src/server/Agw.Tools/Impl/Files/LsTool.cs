using Agw.Files.Abstracts;
using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Files;

public class LsToolParams
{
    [Description(
        """
        The directory to list, relative to the project workspace root.
        """
    )]
    public string Directory { get; set; } = "";

    [Description(
        """
        The search string to match against the names of files in path. This parameter
        can contain a combination of valid literal path and wildcard (* and ?) characters,
        but it doesn't support regular expressions.
        """
    )]
    public string SearchPattern { get; set; } = "*";

    [Description(
        """
        Used to indicate whether to search for files in subfolders
        """
    )]
    public bool Recursion { get; set; } = false;
}

public class LsToolResult
{
    public List<string> Files { get; set; } = new();
}

internal class LsTool : IProjectScopedAgwTool
{
    private readonly IAgwFileSystemResolver _resolver;

    public LsTool(IAgwFileSystemResolver resolver) => _resolver = resolver;

    public string Name => "ls";

    public string Category => "File";

    [Description(
        """
        Returns the names of files (including their paths) that match the specified search
        pattern in the specified directory, using a value to determine whether to search subdirectories.
        """
    )]
    public async Task<LsToolResult> ExecuteAsync(
        LsToolParams toolParams, Guid projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.Directory))
        {
            throw new AgwException(ErrorCodes.DirectoryRequired);
        }

        var fs = await _resolver.ResolveAsync(projectId, ct);
        var files = new List<string>();

        await foreach (var entry in fs.EnumerateAsync(
            toolParams.Directory, toolParams.SearchPattern, toolParams.Recursion, ct))
        {
            ct.ThrowIfCancellationRequested();
            files.Add(entry.Path);
        }

        return new LsToolResult
        {
            Files = files
        };
    }

    public AITool ToAITool() => ToAITool(Guid.Empty);

    public AITool ToAITool(Guid projectId)
    {
        Func<LsToolParams, CancellationToken, Task<LsToolResult>> func =
            (p, ct) => ExecuteAsync(p, projectId, ct);
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }
}
