using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Runtimes.Contracts;

namespace Agw.Agents.Execution.Runtimes;

internal static class ExecutionStartCommandMapper
{
    public static ExecCommand Map(ExecutionStartRequest request) =>
        new(request.Target.AgentType, request.Input)
        {
            ExecutionId = request.ExecutionId,
            AgentId = request.Target.AgentId,
            ConversationId = request.Task.ProjectConversationId,
            Stream = request.Stream,
            ResumeCheckpoint = request.ResumeCheckpoint,
            ResumeGeneration = request.ResumeCheckpoint == null ? null : request.Task.Generation,
        };
}
