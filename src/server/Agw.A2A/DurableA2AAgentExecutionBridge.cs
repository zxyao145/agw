using System.Runtime.CompilerServices;
using A2A;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Contracts;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using AgwTaskProjection = Agw.Shared.Contracts.Projects.TaskProjection;

namespace Agw.A2A;

public interface IDurableA2AExecutionBridge : IAgentExecutionBridge
{
    IAsyncEnumerable<AgwMessage> SubscribeAsync(string taskId, string? cursor, CancellationToken cancellationToken);

    Task<bool> CancelAsync(string taskId, CancellationToken cancellationToken);
}

public sealed class DurableA2AAgentExecutionBridge : IDurableA2AExecutionBridge
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDurableExecutionClient _executionClient;

    public DurableA2AAgentExecutionBridge(IServiceScopeFactory scopeFactory, IDurableExecutionClient executionClient)
    {
        _scopeFactory = scopeFactory;
        _executionClient = executionClient;
    }

    public async Task<AgentExecutionResult?> ExecuteAsync(
        string agentName,
        RequestContext context,
        AgwUserInput input,
        CancellationToken cancellationToken
    )
    {
        var messages = new List<AgwMessage>();
        await foreach (
            var message in ExecuteStreamingAsync(agentName, context, input, cancellationToken).ConfigureAwait(false)
        )
        {
            messages.Add(message);
        }

        return new AgentExecutionResult(context.TaskId, context.ContextId, messages);
    }

    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        string agentName,
        RequestContext context,
        AgwUserInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var executionId = ParseRequiredTaskId(context.TaskId);
        string userId;
        Agent agent;
        AgwTaskProjection task;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            userId = services.GetRequiredService<IUserInfoService>().RequiredUserId;
            var agentRepository = services.GetRequiredService<IRepository<Agent>>();
            agent =
                await agentRepository
                    .SingleOrDefaultAsync(item => item.Name == agentName, cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new AgwException(ErrorCodes.AgentNotFound, $"Agent '{agentName}' not found.");

            var taskAppService = services.GetRequiredService<ITaskAppService>();
            task =
                await taskAppService.GetTaskAsync(executionId).ConfigureAwait(false)
                ?? await taskAppService
                    .CreateTaskForExecutionAsync(
                        ProjectDefaults.A2AId,
                        executionId,
                        GetInputText(input),
                        userId,
                        context.ContextId,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
                ?? throw new AgwException(ErrorCodes.TaskCreationFailed, "Failed to create the A2A task.");
        }

        var settings = ExecutionSettings.FromCommand(
            new SettingCommand(ProjectDefaults.A2AId, contextId: context.ContextId) { Resume = context.IsContinuation }
        );
        await _executionClient
            .StartAsync(
                new DurableExecutionRequest(
                    executionId,
                    userId,
                    agent.Id,
                    Agw.Shared.Data.AgentRuntimeType.Agent,
                    input,
                    task,
                    settings
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        await foreach (
            var executionEvent in _executionClient
                .ReadAsync(executionId, userId, afterCursor: null, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            if (IsHumanInteraction(executionEvent.Message))
            {
                await _executionClient
                    .InterruptAsync(
                        executionId,
                        userId,
                        "A2A execution does not support human interaction.",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                throw new AgwException(
                    ErrorCodes.AgentExecutionFailed,
                    "A2A execution does not support human interaction."
                );
            }
            yield return executionEvent.Message;
        }

        var outcome = await _executionClient
            .GetOutcomeAsync(executionId, userId, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Status == Agw.Shared.Data.Entities.Executions.DurableExecutionStatus.Failed)
        {
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                outcome.ErrorMessage ?? "The A2A execution failed."
            );
        }
    }

    public async IAsyncEnumerable<AgwMessage> SubscribeAsync(
        string taskId,
        string? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var executionId = ParseRequiredTaskId(taskId);
        string userId;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            userId = scope.ServiceProvider.GetRequiredService<IUserInfoService>().RequiredUserId;
        }

        await foreach (
            var executionEvent in _executionClient
                .ReadAsync(executionId, userId, cursor, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return executionEvent.Message;
        }
    }

    public async Task<bool> CancelAsync(string taskId, CancellationToken cancellationToken)
    {
        var executionId = ParseRequiredTaskId(taskId);
        string userId;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            userId = scope.ServiceProvider.GetRequiredService<IUserInfoService>().RequiredUserId;
        }
        return await _executionClient
            .InterruptAsync(executionId, userId, "Canceled through A2A.", cancellationToken)
            .ConfigureAwait(false);
    }

    private static Guid ParseRequiredTaskId(string taskId) =>
        Guid.TryParse(taskId, out var value) ? value : throw new AgwException(ErrorCodes.A2ATaskIdMustBeGuid);

    private static string GetInputText(AgwUserInput input) =>
        string.Join(
            "\n",
            input.Contents.OfType<AgwTextContent>().Select(content => content.Content).Where(value => value != null)
        );

    private static bool IsHumanInteraction(AgwMessage message) =>
        GetMessageType(message) is "human-interaction-request" or "tool-approval-request" or "human-gate-request";

    private static string? GetMessageType(AgwMessage message) => TurnMessageProtocol.GetMessageType(message);
}
