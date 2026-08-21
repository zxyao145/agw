using System.Linq.Expressions;
using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Projects;
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
        var task = new TaskProjection
        {
            TaskId = Guid.CreateVersion7(),
            ProjectConversationId = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            ContextId = "context",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };
        var context = new ExecutionConnectionContext(
            "user",
            "user-id",
            new NullSink(),
            CancellationToken.None,
            runtimeFactory,
            new FakeTaskAppService(task),
            new FakeProjectAppService()
        );
        var connection = new ExecutionConnection(
            "connection",
            "user",
            provider.CreateAsyncScope(),
            new ExecutionCommandDispatcher([]),
            context,
            NullLogger.Instance
        );
        return new ConnectionFixture(connection, context, runtimeFactory);
    }

    private static ExecCommand CreateExecCommand() =>
        new(AgentRuntimeType.Agent, new AgwUserInput { Contents = [] }) { AgentId = Guid.CreateVersion7() };

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

    private sealed class FakeTaskAppService : ITaskAppService
    {
        private readonly TaskProjection _task;

        public FakeTaskAppService(TaskProjection task)
        {
            _task = task;
        }

        public Task<ExecutionTaskResolutionResult> ResolveTaskAsync(
            ExecutionTaskRequest request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new ExecutionTaskResolutionResult(_task, null));

        public Task<TaskProjection?> GetTaskAsync(Guid value) => throw new NotSupportedException();

        public Task<TaskProjection?> CreateTaskForExecutionAsync(
            Guid projectId,
            Guid? taskId,
            string input,
            string user,
            string? contextId = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<bool> HasTaskAsync(
            Guid taskId,
            Guid? projectId = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class FakeProjectAppService : IProjectAppService
    {
        public Task<Project?> GetAsync(Guid id) =>
            Task.FromResult<Project?>(new Project { Id = id, Workspace = "/workspace" });

        public Task<IReadOnlyList<Project>> ListAsync(Expression<Func<Project, bool>>? predicate = null) =>
            throw new NotSupportedException();

        public Task<string?> GetProjectExtraSettingAsync(Guid? projectId) => throw new NotSupportedException();

        public Task<Guid?> ResolveProjectIdAsync(Guid? projectId) => throw new NotSupportedException();

        public Task<Project?> CreateAsync(Project project, string user) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id) => throw new NotSupportedException();

        public Task<Project?> UpdateAsync(Guid id, Action<Project> updateAction, string user) =>
            throw new NotSupportedException();
    }

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
