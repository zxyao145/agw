using Agw.Agents.Execution.Commands.Abstracts;

namespace Agw.Agents.Execution.Commands.Interrupt;

public class InterruptCommand : AgentRunCommand
{
    /// <summary>
    /// 获取或设置需要中断的 durable execution；进程内模式可省略。
    /// </summary>
    public Guid? ExecutionId { get; set; }

    public string? Reason { get; set; }
}
