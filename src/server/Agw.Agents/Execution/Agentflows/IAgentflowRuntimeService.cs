using Agw.Shared.AgwMsgVm;

using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agentflows;

public interface IAgentflowRuntimeService
{
    IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        Guid agentflowId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null,
        Guid? taskId = null,
        IHumanGateApprovalHandler? humanGateApprovalHandler = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null);

    Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        Guid taskId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null);

    Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        Guid taskId,
        List<ChatMessage> messages,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null);

    Task<AgentflowWorkflowLease?> CreateAiWorkflow(Guid agentflowId, CancellationToken cancellationToken = default);

    Task<string?> GetMermaidAsync(Guid agentflowId, CancellationToken cancellationToken = default);
}
