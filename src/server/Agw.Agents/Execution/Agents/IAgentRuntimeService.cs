using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Runtimes;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Exceptions;

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

    Task<AIAgent?> CreateAiAgentAsync(
        Guid agentId,
        Guid? projectId,
        bool resume,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken = default);

    Task<AIAgent?> CreateAgentflowNodeAgentAsync(
        Guid agentId,
        Guid? projectId,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken = default) =>
        CreateAiAgentAsync(
            agentId,
            projectId,
            resume: false,
            environmentVariables,
            cancellationToken);

    Task<AIAgent?> CreateAgentflowNodeAgentAsync(
        Guid agentId,
        Guid? projectId,
        Guid conversationId,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken = default) =>
        CreateAgentflowNodeAgentAsync(
            agentId,
            projectId,
            environmentVariables,
            cancellationToken);

    Task<AgentRuntime?> CreateRuntimeAsync(
        Guid agentId,
        TaskProjection task,
        SettingCommand settings,
        CancellationToken cancellationToken = default);

    Task SetModeAsync(
        AgentRuntime runtime,
        string mode,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new AgwException(
            ErrorCodes.InvalidParam,
            "The Agent runtime service does not support mode changes."));

    Task SetPermissionModeAsync(
        AgentRuntime runtime,
        PermissionMode permissionMode,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentRuntime session,
        AgwUserInput input,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentRuntime session,
        AgwUserInput input,
        IHumanGateApprovalHandler? approvalHandler,
        CancellationToken cancellationToken = default) =>
        ExecuteStreamingAsync(session, input, cancellationToken);

    Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgentRuntime session,
        AgwUserInput input,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgentRuntime session,
        AgwUserInput input,
        IHumanGateApprovalHandler? approvalHandler,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(session, input, cancellationToken);

    Task<AgentExecutionResult?> ExecuteByIdAsync(AgentExecuteByIdRequest request, CancellationToken cancellationToken = default);
}
