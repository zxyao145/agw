using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Runtime.AgentRun.Dtos;

public partial record AgentExecutionResult(
    string TaskId,
    string ContextId,
    IReadOnlyList<AgwMessage> Messages);

public partial record AgentExecutionResult
{
    public AgentExecutionResult(
        string taskId,
        IReadOnlyList<AgwMessage> messages)
        : this(taskId, taskId, messages)
    {
    }
}
