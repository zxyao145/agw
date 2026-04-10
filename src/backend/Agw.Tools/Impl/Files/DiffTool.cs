using System.Text;

using Agw.Shared.Contracts.Tools.Abstractions;

using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Files;

public class DiffToolParams
{
    [Description(
    """
        the before text.
    """
    )]
    public string Before { get; set; } = "";

    [Description(
    """
        the after text.
    """
    )]
    public string After { get; set; } = "";
}

public class DiffToolResult
{
    public string Result { get; set; } = "";
}

internal class DiffTool : IAgwTool
{
    public string Name => "diff";

    [Description(
    """
    Generate the difference between the two texts before and after.
    """
    )]
    public DiffToolResult Execute(DiffToolParams toolParams)
    {
        var diff = InlineDiffBuilder.Diff(toolParams.Before, toolParams.After);
        StringBuilder sb = new StringBuilder();

        foreach (var line in diff.Lines)
        {
            switch (line.Type)
            {
                case ChangeType.Inserted:
                    sb.Append("+ ");
                    break;

                case ChangeType.Deleted:
                    sb.Append("- ");
                    break;

                default:
                    sb.Append("  ");
                    break;
            }

            sb.AppendLine(line.Text);
        }

        var res = new DiffToolResult
        {
            Result = sb.ToString()
        };
        return res;
    }

    public AITool ToAITool()
    {
        Func<DiffToolParams, DiffToolResult> func = Execute;
        var aiTool = AIFunctionFactory.Create(func, Name);
        return aiTool;
    }
}
