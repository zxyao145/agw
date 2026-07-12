using Agw.Agents.Runtime.AgentRun.Dtos;
using Agw.Agents.Runtime.Contracts;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Tasks;

using Microsoft.Agents.AI;

namespace Agw.Agents.Runtime.AgentRun;

public interface IAgentRuntimeService
{
    Task<AIAgent?> CreateAiAgentAsync(Guid agentId, CancellationToken cancellationToken = default);

    Task<AIAgent?> CreateAiAgentAsync(
        Guid agentId,
        Guid? projectId,
        bool resume,
        CancellationToken cancellationToken = default);

    Task<AgentExecSession?> CreateSessionAsync(
        Guid agentId,
        TaskProjection task,
        SettingCommand settings,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentExecSession session,
        AgwUserInput input,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgentExecSession session,
        AgwUserInput input,
        CancellationToken cancellationToken = default);

    Task<AgentExecutionResult?> ExecuteByIdAsync(AgentExecuteByIdRequest request, CancellationToken cancellationToken = default);
}
