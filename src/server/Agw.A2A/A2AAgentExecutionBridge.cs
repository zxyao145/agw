using System.Runtime.CompilerServices;
using A2A;
using Agw.Agents.Contracts.Execution;
using Agw.Auth.Contracts;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.A2A;

public sealed record AgentExecutionResult(string TaskId, string ContextId, IReadOnlyList<AgwMessage> Messages);

public interface IAgentExecutionBridge
{
    Task<AgentExecutionResult> ExecuteAsync(
        string agentName,
        RequestContext context,
        AgwUserInput input,
        CancellationToken cancellationToken
    );

    IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        string agentName,
        RequestContext context,
        AgwUserInput input,
        CancellationToken cancellationToken
    );
}

public interface IDurableA2AExecutionBridge : IAgentExecutionBridge
{
    bool SupportsDurableOperations { get; }

    IAsyncEnumerable<AgwMessage> SubscribeAsync(string taskId, string? cursor, CancellationToken cancellationToken);

    Task<bool> CancelAsync(string taskId, CancellationToken cancellationToken);
}

public sealed class A2AAgentExecutionBridge : IDurableA2AExecutionBridge
{
    private readonly IServiceScopeFactory _scopeFactory;

    public A2AAgentExecutionBridge(IServiceScopeFactory scopeFactory, bool supportsDurableOperations)
    {
        _scopeFactory = scopeFactory;
        SupportsDurableOperations = supportsDurableOperations;
    }

    public bool SupportsDurableOperations { get; }

    public async Task<AgentExecutionResult> ExecuteAsync(
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
        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var ownerUserId = services.GetRequiredService<IUserInfoService>().RequiredUserId;
        var task = await services
            .GetRequiredService<IProjectTaskFacade>()
            .GetOrCreateAsync(
                new StartProjectTaskRequest(
                    ProjectDefaults.A2AId,
                    executionId,
                    JobId: null,
                    GetInputText(input),
                    agentName,
                    context.ContextId,
                    ownerUserId,
                    ProjectTaskStatus.Running
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        var executions = services.GetRequiredService<IAgentExecutionFacade>();
        await foreach (
            var executionEvent in executions
                .ExecuteStreamingAsync(
                    new Agw.Agents.Contracts.Execution.AgentExecutionRequest(
                        executionId,
                        ownerUserId,
                        new AgentTarget(AgentTargetKind.Agent, Name: agentName),
                        task,
                        input,
                        context.IsContinuation,
                        HumanInteractionPolicy.Reject
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false)
        )
        {
            yield return executionEvent.Message;
        }
    }

    public async IAsyncEnumerable<AgwMessage> SubscribeAsync(
        string taskId,
        string? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var executionId = ParseRequiredTaskId(taskId);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var ownerUserId = services.GetRequiredService<IUserInfoService>().RequiredUserId;
        await foreach (
            var executionEvent in services
                .GetRequiredService<IDurableAgentExecutionFacade>()
                .SubscribeAsync(executionId, ownerUserId, cursor, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return executionEvent.Message;
        }
    }

    public async Task<bool> CancelAsync(string taskId, CancellationToken cancellationToken)
    {
        var executionId = ParseRequiredTaskId(taskId);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var ownerUserId = services.GetRequiredService<IUserInfoService>().RequiredUserId;
        return await services
            .GetRequiredService<IDurableAgentExecutionFacade>()
            .InterruptAsync(executionId, ownerUserId, "Canceled through A2A.", cancellationToken)
            .ConfigureAwait(false);
    }

    private static Guid ParseRequiredTaskId(string taskId) =>
        Guid.TryParse(taskId, out var value) ? value : throw new AgwException(ErrorCodes.A2ATaskIdMustBeGuid);

    private static string GetInputText(AgwUserInput input) =>
        string.Join(
            "\n",
            input.Contents.OfType<AgwTextContent>().Select(content => content.Content).Where(value => value != null)
        );
}
