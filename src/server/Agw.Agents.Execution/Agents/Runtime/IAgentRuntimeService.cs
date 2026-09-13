using Agw.Agents.Execution.Agents.Contracts;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.Runtimes;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using RuntimeAgentExecutionResult = Agw.Agents.Execution.Agents.Contracts.AgentExecutionResult;

namespace Agw.Agents.Execution.Agents.Runtime;

public interface IAgentRuntimeService
{
    /// <summary>Only reuse runtimes whose definition is still current. Implementations without version checks rebuild.</summary>
    Task<bool> IsRuntimeCurrentAsync(AgentRuntime runtime, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<AIAgent?> CreateAiAgentAsync(Guid agentId, CancellationToken cancellationToken = default);

    Task<AIAgent?> CreateAiAgentAsync(
        Guid agentId,
        Guid? projectId,
        bool resume,
        CancellationToken cancellationToken = default
    );

    Task<AIAgent?> CreateAiAgentAsync(
        Guid agentId,
        Guid? projectId,
        bool resume,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken = default
    );

    Task<AIAgent?> CreateAgentflowNodeAgentAsync(
        Guid agentId,
        Guid? projectId,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken = default
    ) => CreateAiAgentAsync(agentId, projectId, resume: false, environmentVariables, cancellationToken);

    Task<AIAgent?> CreateAgentflowNodeAgentAsync(
        Guid agentId,
        Guid? projectId,
        Guid conversationId,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken = default
    ) => CreateAgentflowNodeAgentAsync(agentId, projectId, environmentVariables, cancellationToken);

    /// <summary>
    /// 创建 Agentflow node Agent，并允许 durable 调用方延迟人机交互 Tool。
    /// </summary>
    Task<AIAgent?> CreateAgentflowNodeAgentAsync(
        Guid agentId,
        Guid? projectId,
        Guid conversationId,
        IReadOnlyDictionary<string, string>? environmentVariables,
        bool deferHumanInteractions,
        CancellationToken cancellationToken = default,
        AgwPermissionMode? permissionMode = null
    ) =>
        deferHumanInteractions || permissionMode.HasValue
            ? Task.FromException<AIAgent?>(
                new AgwException(
                    ErrorCodes.InvalidParam,
                    "The Agent runtime service does not support permission snapshots or deferred interactions."
                )
            )
            : CreateAgentflowNodeAgentAsync(
                agentId,
                projectId,
                conversationId,
                environmentVariables,
                cancellationToken
            );

    Task<AgentRuntime?> CreateRuntimeAsync(
        Guid agentId,
        AgentExecutionTask task,
        ExecutionSettings settings,
        CancellationToken cancellationToken = default
    );

    Task SetModeAsync(AgentRuntime runtime, string mode, CancellationToken cancellationToken = default) =>
        Task.FromException(
            new AgwException(ErrorCodes.InvalidParam, "The Agent runtime service does not support mode changes.")
        );

    Task SetPermissionModeAsync(
        AgentRuntime runtime,
        AgwPermissionMode permissionMode,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromException(
            new AgwException(ErrorCodes.InvalidParam, "The Agent runtime service does not support permission changes.")
        );

    IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentRuntime session,
        AgwUserInput input,
        CancellationToken cancellationToken = default
    );

    IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentRuntime session,
        AgwUserInput input,
        IInteractionHandler? approvalHandler,
        CancellationToken cancellationToken = default
    ) =>
        approvalHandler == null
            ? ExecuteStreamingAsync(session, input, cancellationToken)
            : throw new AgwException(
                ErrorCodes.InvalidParam,
                "The Agent runtime service does not support approval handlers."
            );

    Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgentRuntime session,
        AgwUserInput input,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgentRuntime session,
        AgwUserInput input,
        IInteractionHandler? approvalHandler,
        CancellationToken cancellationToken = default
    ) =>
        approvalHandler == null
            ? ExecuteAsync(session, input, cancellationToken)
            : Task.FromException<IReadOnlyList<AgwMessage>>(
                new AgwException(
                    ErrorCodes.InvalidParam,
                    "The Agent runtime service does not support approval handlers."
                )
            );

    Task<RuntimeAgentExecutionResult?> ExecuteByIdAsync(
        AgentExecuteByIdRequest request,
        CancellationToken cancellationToken = default
    );
}
