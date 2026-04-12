using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Contracts;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Models;

using Microsoft.Agents.AI;

namespace Agw.Agents.Application.AgentRun;

public interface IAgentRuntimeService
{
    Task<AIAgent?> CreateAiAgentAsync(Guid agentId, string? extraOverride = null, CancellationToken cancellationToken = default);

    Task<AgentExecSession?> CreateSessionAsync(
        Guid agentId,
        ProjectTask task,
        SettingCommand settings,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentExecSession session,
        AgwUserInput input,
        CancellationToken cancellationToken = default);

    Task<AgentExecutionResult?> ExecuteByNameAsync(AgentExecuteByNameRequest request, CancellationToken cancellationToken = default);

    Task<AgentExecutionResult?> ExecuteByIdAsync(AgentExecuteByIdRequest request, CancellationToken cancellationToken = default);
}
