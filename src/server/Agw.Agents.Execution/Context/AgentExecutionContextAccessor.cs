using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Context;

/// <summary>
/// MAF 调用链内读取运行选项中的执行数据；其他执行阶段读取 Host 执行作用域。
/// Reads the execution data from run options inside the MAF call chain and from the Host execution scope elsewhere.
/// </summary>
public sealed class AgentExecutionContextAccessor : IAgentExecutionContextAccessor
{
    public AgentExecutionContext? Current =>
        AIAgent.CurrentRunContext is { } run
            ? ExecutionRunOptions.Read(run.RunOptions)
            : ExecutionScope.Current?.Context;

    public AgentExecutionContext Required => Current ?? throw new AgwException(ErrorCodes.ExecutionContextMissing);
}
