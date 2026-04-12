using Agw.Agents.Domain.Entities;
using Agw.Agents.Application;
using Agw.Api.Contracts;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Models;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using ModelContextProtocol.Client;

namespace Agw.Appliaction.Services.Agents;

public interface IAgentRuntimeService
{
    Task<IReadOnlyList<Agent>> ListAgentsAsync();

    Task<Agent?> GetAgentAsync(Guid id);

    Task<Agent?> CreateAgentAsync(
        Agent agent,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        IEnumerable<Guid>? appInstanceIds,
        string user);

    Task<Agent?> UpdateAgentAsync(
        Guid id,
        Action<Agent> updateAction,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        IEnumerable<Guid>? appInstanceIds,
        string user);

    Task<bool> DeleteAgentAsync(Guid id);

    Task<IReadOnlyList<McpServer>> ListMcpToolServersAsync();

    Task<McpServer?> GetMcpToolServerAsync(Guid id);

    Task<McpServer> CreateMcpToolServerAsync(McpServer server, IEnumerable<Guid>? agentIds, string user);

    Task<McpServer?> UpdateMcpToolServerAsync(Guid id, Action<McpServer> updateAction, string user);

    Task<bool> DeleteMcpToolServerAsync(Guid id);

    Task<IReadOnlyList<McpClientTool>> ListMcpToolsAsync(Guid mcpToolServerId, CancellationToken cancellationToken = default);

    Task<AIAgent?> CreateAiAgentAsync(
        Guid agentId,
        string? systemPrompt = null,
        string? extraOverride = null,
        CancellationToken cancellationToken = default);

    Task<AIAgent?> CreateAiAgentAsync(CreateAiAgentRequest request);

    Task<AgentExecSession?> CreateSessionAsync(
        Guid agentId,
        ProjectTask task,
        SettingCommand settings,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentExecSession session,
        AgwUserInput input,
        CancellationToken cancellationToken = default);

    Task<AgentExecutionResult?> ExecuteByNameAsync(
        string agentName,
        Guid? taskId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null);

    Task<AgentExecutionResult?> ExecuteAsync(
        Guid agentId,
        Guid taskId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null);

    Task<AgentExecutionResult?> ExecuteAsync(
        Guid agentId,
        Guid taskId,
        List<ChatMessage> input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null);
}
