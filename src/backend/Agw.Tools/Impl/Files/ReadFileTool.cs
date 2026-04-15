using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Files;

public class ReadFileParam
{
    [Description(
        """
        The path to the file.
        """
    )]
    public string FilePath { get; set; } = "";
}

public class ReadFileResult
{
    public string Content { get; set; } = "";
}

internal class ReadFileTool : IAgwTool
{
    public string Name => "read_file";
    public bool ApprovalRequired => false;

    [Description(
        """
        Reads a file from the local filesystem. You can access any file directly by using this tool.
        """
    )]
    public ReadFileResult Execute(ReadFileParam readFileParam)
    {
        ArgumentNullException.ThrowIfNull(readFileParam);

        if (string.IsNullOrWhiteSpace(readFileParam.FilePath))
        {
            throw new AgwException(ErrorCodes.FilePathRequired);
        }
        if (!File.Exists(readFileParam.FilePath))
        {
            throw new AgwException(ErrorCodes.FileNotFound, $"File '{readFileParam.FilePath}' does not exist.");
        }
        var content = File.ReadAllText(readFileParam.FilePath);

        var res = new ReadFileResult
        {
            Content = content,
        };
        return res;
    }

    public AITool ToAITool()
    {
        Func<ReadFileParam, ReadFileResult> func = Execute;
        var aiTool = AIFunctionFactory.Create(func, Name);
        if (ApprovalRequired)
        {
#pragma warning disable MEAI001 // 类型仅用于评估，在将来的更新中可能会被更改或删除。取消此诊断以继续。
            aiTool = new ApprovalRequiredAIFunction(aiTool);
#pragma warning restore MEAI001 // 类型仅用于评估，在将来的更新中可能会被更改或删除。取消此诊断以继续。
        }
        return aiTool;
    }
}
