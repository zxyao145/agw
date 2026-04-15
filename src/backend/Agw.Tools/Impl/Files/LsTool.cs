using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Files;

public class LsToolParams
{
    [Description(
        """
        The relative or absolute path to the directory to search. This string is not case-sensitive.
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

internal class LsTool : IAgwTool
{
    public string Name => "ls";

    [Description(
        """
        Returns the names of files (including their paths) that match the specified search
        pattern in the specified directory, using a value to determine whether to search subdirectories.
        """
    )]
    public LsToolResult Execute(LsToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.Directory))
        {
            throw new AgwException(ErrorCodes.DirectoryRequired);
        }
        if (!Directory.Exists(toolParams.Directory))
        {
            throw new AgwException(ErrorCodes.DirectoryNotFound, $"Directory '{toolParams.Directory}' does not exist.");
        }

        var files = Directory.GetFiles(toolParams.Directory, toolParams.SearchPattern, toolParams.Recursion ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        var res = new LsToolResult
        {
            Files = new List<string>(files)
        };
        return res;
    }

    public AITool ToAITool()
    {
        Func<LsToolParams, LsToolResult> func = Execute;
        var aiTool = AIFunctionFactory.Create(func, Name);
        return aiTool;
    }
}
