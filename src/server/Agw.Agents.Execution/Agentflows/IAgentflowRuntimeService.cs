using Agw.Agents.Contracts.Catalog;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agentflows;

public interface IAgentflowRuntimeService : IAgentflowMermaidProvider
{
    IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        Guid agentflowId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null,
        Guid? taskId = null,
        IHumanGateApprovalHandler? humanGateApprovalHandler = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        Guid? conversationId = null,
        PermissionMode? permissionMode = null
    );

    Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        Guid taskId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null,
        PermissionMode? permissionMode = null
    );

    Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        Guid taskId,
        List<ChatMessage> messages,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null,
        PermissionMode? permissionMode = null
    );

    Task<AgentflowWorkflowLease?> CreateAiWorkflow(Guid agentflowId, CancellationToken cancellationToken = default);
}
