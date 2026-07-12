using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Contracts;
using Agw.Agents.Execution.Runtimes;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Tasks;

using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Agents;

public interface IAgentRuntimeService
{
    Task<AIAgent?> CreateAiAgentAsync(Guid agentId, CancellationToken cancellationToken = default);

    Task<AIAgent?> CreateAiAgentAsync(
        Guid agentId,
        Guid? projectId,
        bool resume,
        CancellationToken cancellationToken = default);

    Task<AgentRuntime?> CreateRuntimeAsync(
        Guid agentId,
        TaskProjection task,
        SettingCommand settings,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentRuntime session,
        AgwUserInput input,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgentRuntime session,
        AgwUserInput input,
        CancellationToken cancellationToken = default);

    Task<AgentExecutionResult?> ExecuteByIdAsync(AgentExecuteByIdRequest request, CancellationToken cancellationToken = default);
}
