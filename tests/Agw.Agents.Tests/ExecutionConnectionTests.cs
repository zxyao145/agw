using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class ExecutionConnectionTests
{
    [Fact]
    public async Task DetachAsync_IdleRuntime_DisposesAndRemovesImmediately()
    {
        var fixture = CreateFixture(holdTurnOpen: false);
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(CreateExecCommand(), TestContext.Current.CancellationToken);
        var removed = false;

        await connection.DetachAsync(() => removed = true);

        Assert.True(fixture.RuntimeFactory.Runtime.Disposed);
        Assert.True(removed);
    }

    [Fact]
    public async Task DetachAsync_RunningTurn_RemovesAfterTurnCompletes()
    {
        var fixture = CreateFixture(holdTurnOpen: true);
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(CreateExecCommand(), TestContext.Current.CancellationToken);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await connection.DetachAsync(() => removed.TrySetResult());
        Assert.False(removed.Task.IsCompleted);

        fixture.RuntimeFactory.CompleteTurn();
        await removed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.RuntimeFactory.Runtime.Disposed);
    }

    [Fact]
    public async Task DetachAsync_WaitingForHuman_InterruptsTurn()
    {
        var fixture = CreateFixture(holdTurnOpen: true);
        await using var connection = fixture.Connection;
        await fixture.Context.StartTurnAsync(CreateExecCommand(), TestContext.Current.CancellationToken);
        fixture.RuntimeFactory.StartRequest!.TurnContext.PendingHumanGateChanged!(
            new HumanGateApprovalRequest("request", "node", null, "approval", "approve?", [])
        );
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await connection.DetachAsync(() => removed.TrySetResult());

        Assert.True(fixture.RuntimeFactory.TurnCancellation!.IsCancellationRequested);
        fixture.RuntimeFactory.CompleteTurn();
        await removed.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private static ConnectionFixture CreateFixture(bool holdTurnOpen)
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var runtimeFactory = new FakeRuntimeFactory(holdTurnOpen);
        var task = new AgentExecutionTask
        {
            TaskId = Guid.CreateVersion7(),
            ProjectConversationId = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            ContextId = "context",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };
        var context = new ExecutionConnectionContext(
            "user-id",
            new NullSink(),
            CancellationToken.None,
            runtimeFactory,
            new FakeProjectTaskFacade(task),
            new FakeProjectRuntimeFacade()
        );
        var connection = new ExecutionConnection(
            "connection",
            "user-id",
            provider.CreateAsyncScope(),
            new ExecutionCommandDispatcher([]),
            context,
            NullLogger.Instance
        );
        Assert.Equal("user-id", connection.UserId);
        return new ConnectionFixture(connection, context, runtimeFactory);
    }

    private static ExecCommand CreateExecCommand() =>
        new(AgentRuntimeType.Agent, new AgwUserInput { Contents = [] })
        {
            AgentId = Guid.CreateVersion7(),
            ConversationId = Guid.CreateVersion7(),
        };

    private sealed record ConnectionFixture(
        ExecutionConnection Connection,
        ExecutionConnectionContext Context,
        FakeRuntimeFactory RuntimeFactory
    );

    private sealed class FakeRuntimeFactory : IRuntimeFactory
    {
        private readonly bool _holdTurnOpen;
        private TaskCompletionSource? _completion;

        public FakeRuntimeFactory(bool holdTurnOpen)
        {
            _holdTurnOpen = holdTurnOpen;
        }

        public TestRuntime Runtime { get; } = new();

        public CancellationTokenSource? TurnCancellation { get; private set; }

        public RuntimeStartRequest? StartRequest { get; private set; }

        public Task<RuntimeStartResult> StartAsync(RuntimeStartRequest request, CancellationToken cancellationToken)
        {
            StartRequest = request;
            if (!_holdTurnOpen)
            {
                return Task.FromResult(
                    new RuntimeStartResult(Runtime, new ActiveTurn(Task.CompletedTask, new CancellationTokenSource()))
                );
            }

            _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TurnCancellation = new CancellationTokenSource();
            var turn = new ActiveTurn(_completion.Task, TurnCancellation);
            Runtime.TryStartTurn(turn);
            return Task.FromResult(new RuntimeStartResult(Runtime, turn));
        }

        public void CompleteTurn() => _completion!.TrySetResult();
    }

    private sealed class FakeProjectTaskFacade : IProjectTaskFacade
    {
        public Task<int?> GetGenerationAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(0);

        private readonly ProjectTaskSnapshot _task;

        public FakeProjectTaskFacade(AgentExecutionTask task)
        {
            _task = ToSnapshot(task);
        }

        public Task<ProjectTaskSnapshot> ResolveAsync(
            ResolveProjectTaskRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(_task with { ProjectConversationId = request.ConversationId });

        public Task<ProjectTaskSnapshot?> GetAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectTaskSnapshot> GetOrCreateAsync(
            StartProjectTaskRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<ProjectTaskSnapshot?> FinishAsync(
            FinishProjectTaskRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, string?>> ResolveContextIdsAsync(
            IReadOnlyCollection<Guid> taskIds,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class FakeProjectRuntimeFacade : IProjectRuntimeFacade
    {
        public Task<ProjectRuntimeSnapshot?> GetForCurrentUserAsync(
            Guid projectId,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult<ProjectRuntimeSnapshot?>(
                new ProjectRuntimeSnapshot(
                    projectId,
                    "project",
                    "/workspace",
                    null,
                    [],
                    new Dictionary<string, string>(),
                    [],
                    [],
                    []
                )
            );

        public Task<string?> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("/workspace");
    }

    private static ProjectTaskSnapshot ToSnapshot(AgentExecutionTask task) =>
        new(
            task.TaskId,
            task.ProjectConversationId,
            task.ProjectId,
            task.ContextId,
            task.JobId,
            task.Title,
            ProjectTaskStatus.Pending,
            task.ErrorMessage,
            task.CreateTime,
            task.UpdateTime,
            task.FinishedTime
        );

    private sealed class TestRuntime : RuntimeBase
    {
        public bool Disposed { get; private set; }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Disposed = true;
        }
    }

    private sealed class NullSink : IExecutionMessageSink
    {
        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
