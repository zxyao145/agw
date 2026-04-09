using System.Net.WebSockets;

using Agw.Agents.Application;
using Agw.Api.Contracts;
using Agw.Shared.Data.Entities.Tasks;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Api.Execution;

public sealed record ExecutionTaskRequest(
    Guid ExecutionId,
    AgentRuntimeType AgentType,
    Guid? TaskId,
    Guid? ProjectId,
    string Input,
    bool Resume,
    string User);

public readonly record struct ExecutionTaskResolutionResult(ProjectTask? Task, IActionResult? Error);

public sealed record StreamingExecutionStartRequest(
    Guid AgentId,
    ProjectTask Task,
    ExecCommand Command,
    AgentExecSession? CurrentSession,
    SettingCommand Settings,
    WebSocket WebSocket,
    SemaphoreSlim SendLock);

public readonly record struct ExecutionStartResult(
    AgentExecSession? AgentSession,
    ActiveExecution? ActiveExecution);

public interface IAgentExecutionCoordinator
{
    Task<SettingCommand> NormalizeSettingsAsync(SettingCommand settings, CancellationToken cancellationToken);

    Task<ExecutionTaskResolutionResult> ResolveTaskAsync(ExecutionTaskRequest request, CancellationToken cancellationToken);

    Task<ExecutionStartResult> StartStreamingExecutionAsync(
        StreamingExecutionStartRequest request,
        CancellationToken cancellationToken);
}
