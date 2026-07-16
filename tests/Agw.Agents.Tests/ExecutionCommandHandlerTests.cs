using System.Linq.Expressions;

using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Contracts;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using Agw.Files.Utils;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Projects;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class ExecutionCommandHandlerTests
{
    [Fact]
    public async Task SettingCommand_ChangedSettings_ReleasesRuntimeAndClearsResolvedState()
    {
        var runtime = new TestRuntime();
        await using var connection = CreateConnection(new SettingCommandHandler());
        connection.Settings = new SettingCommand(Guid.CreateVersion7(), contextId: "old");
        connection.ResolvedTask = CreateTask("old");
        connection.Runtime = runtime;

        await connection.DispatchAsync(
            new SettingCommand(Guid.CreateVersion7(), contextId: "new"),
            TestContext.Current.CancellationToken);

        Assert.True(runtime.Disposed);
        Assert.Null(connection.Runtime);
        Assert.Null(connection.ResolvedTask);
        Assert.Equal("new", connection.Settings!.ContextId);
    }

    [Fact]
    public async Task SettingCommand_ActiveTurn_SendsBusyWithoutChangingSettings()
    {
        var sink = new CapturingSink();
        var runtime = new TestRuntime();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        runtime.TryStartTurn(new ActiveTurn(completion.Task, cts));
        await using var connection = CreateConnection(new SettingCommandHandler(), sink);
        var current = new SettingCommand(Guid.CreateVersion7(), contextId: "current");
        connection.Settings = current;
        connection.Runtime = runtime;

        await connection.DispatchAsync(
            new SettingCommand(Guid.CreateVersion7(), contextId: "new"),
            TestContext.Current.CancellationToken);

        Assert.Same(current, connection.Settings);
        Assert.Same(runtime, connection.Runtime);
        Assert.IsType<AgwErrorContent>(Assert.Single(sink.Messages).Contents[0]);

        completion.SetResult();
        await runtime.WhenIdleAsync();
    }

    [Fact]
    public async Task InterruptCommand_WithoutActiveTurn_SendsExistingSystemMessage()
    {
        var sink = new CapturingSink();
        await using var connection = CreateConnection(new InterruptCommandHandler(), sink);

        await connection.DispatchAsync(
            new InterruptCommand { Reason = "nothing running" },
            TestContext.Current.CancellationToken);

        Assert.Equal("nothing running", Assert.IsType<AgwTextContent>(Assert.Single(sink.Messages).Contents[0]).Content);
    }

    [Fact]
    public async Task InterruptCommand_WithActiveTurn_ForwardsCancellation()
    {
        var runtime = new TestRuntime();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        runtime.TryStartTurn(new ActiveTurn(completion.Task, cts));
        await using var connection = CreateConnection(new InterruptCommandHandler());
        connection.Runtime = runtime;

        await connection.DispatchAsync(
            new InterruptCommand { Reason = "stop" },
            TestContext.Current.CancellationToken);

        Assert.True(cts.IsCancellationRequested);

        completion.SetResult();
        await runtime.WhenIdleAsync();
    }

    [Fact]
    public async Task HumanResponseCommand_WithoutPendingGate_SendsExistingSystemMessage()
    {
        var sink = new CapturingSink();
        await using var connection = CreateConnection(new HumanResponseCommandHandler(), sink);

        await connection.DispatchAsync(
            new HumanResponseCommand("missing", approved: true),
            TestContext.Current.CancellationToken);

        var content = Assert.IsType<AgwTextContent>(Assert.Single(sink.Messages).Contents[0]);
        Assert.Equal("No matching HumanGate request is waiting for this response.", content.Content);
    }

    [Fact]
    public async Task ExecCommand_ReusesResolvedTaskAndRuntimeForSameTarget()
    {
        var factory = new FakeRuntimeFactory(CreateTask("resolved"));
        var handler = CreateExecCommandHandler(factory);
        await using var connection = CreateConnection(handler);
        var agentId = Guid.CreateVersion7();
        var command = CreateExecCommand(agentId);

        await connection.DispatchAsync(command, TestContext.Current.CancellationToken);
        await connection.DispatchAsync(command, TestContext.Current.CancellationToken);

        Assert.Equal(1, factory.ResolveCount);
        Assert.Equal(2, factory.StartRequests.Count);
        Assert.Null(factory.StartRequests[0].CurrentRuntime);
        Assert.Same(factory.StartRequests[0].TurnContext.Settings, connection.Settings);
        Assert.Same(connection.Runtime, factory.StartRequests[1].CurrentRuntime);
    }

    [Fact]
    public async Task ExecCommand_ChangedTarget_ReleasesPreviousRuntime()
    {
        var factory = new FakeRuntimeFactory(CreateTask("resolved"));
        var handler = CreateExecCommandHandler(factory);
        await using var connection = CreateConnection(handler);
        var previous = new TestRuntime();
        connection.Runtime = previous;
        connection.Target = new ExecutionTarget(Guid.CreateVersion7(), AgentRuntimeType.Agent);
        connection.Settings = new SettingCommand(Guid.CreateVersion7());
        connection.ResolvedTask = CreateTask("resolved");

        await connection.DispatchAsync(
            CreateExecCommand(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        Assert.True(previous.Disposed);
        Assert.Null(factory.StartRequests[0].CurrentRuntime);
    }

    [Fact]
    public async Task ExecCommand_ProjectWorkspaceWithTilde_AddsExpandedAbsoluteWorkspaceToTurnContext()
    {
        var factory = new FakeRuntimeFactory(CreateTask("resolved"));
        const string configuredWorkspace = "~/.agw/runtime-context-test";
        var handler = CreateExecCommandHandler(factory, configuredWorkspace);
        await using var connection = CreateConnection(handler);

        await connection.DispatchAsync(
            CreateExecCommand(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        var expectedWorkspace = Path.GetFullPath(PathUtil.ExpandTilde(configuredWorkspace));
        Assert.Equal(expectedWorkspace, Assert.Single(factory.StartRequests).TurnContext.Workspace);
    }

    private static ExecCommandHandler CreateExecCommandHandler(
        IRuntimeFactory runtimeFactory,
        string workspace = "~/.agw/temp") =>
        new(runtimeFactory, new FakeProjectAppService(workspace));

    private static ExecutionConnection CreateConnection(
        IExecutionCommandHandler handler,
        IExecutionMessageSink? sink = null)
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        return new ExecutionConnection(
            "connection",
            "user",
            scope,
            new ExecutionCommandDispatcher([handler]),
            sink ?? new CapturingSink(),
            CancellationToken.None,
            NullLogger.Instance);
    }

    private static ExecCommand CreateExecCommand(Guid agentId) =>
        new(
            AgentRuntimeType.Agent,
            new AgwUserInput
            {
                Contents = [new AgwTextContent { Content = "hello" }],
            })
        {
            AgentId = agentId,
        };

    private static TaskProjection CreateTask(string contextId) =>
        new()
        {
            TaskId = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            ContextId = contextId,
            Title = "test",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private sealed class FakeRuntimeFactory : IRuntimeFactory
    {
        private readonly TaskProjection _task;

        public FakeRuntimeFactory(TaskProjection task)
        {
            _task = task;
        }

        public int ResolveCount { get; private set; }

        public List<RuntimeStartRequest> StartRequests { get; } = [];

        public Task<ExecutionTaskResolutionResult> ResolveTaskAsync(
            ExecutionTaskRequest request,
            CancellationToken cancellationToken)
        {
            ResolveCount++;
            return Task.FromResult(new ExecutionTaskResolutionResult(_task, null));
        }

        public Task<RuntimeStartResult> StartAsync(
            RuntimeStartRequest request,
            CancellationToken cancellationToken)
        {
            StartRequests.Add(request);
            var runtime = request.CurrentRuntime ?? new TestRuntime();
            var activeTurn = new ActiveTurn(Task.CompletedTask, new CancellationTokenSource());
            return Task.FromResult(new RuntimeStartResult(runtime, activeTurn));
        }
    }

    private sealed class FakeProjectAppService : IProjectAppService
    {
        private readonly string _workspace;

        public FakeProjectAppService(string workspace)
        {
            _workspace = workspace;
        }

        public Task<Project?> GetAsync(Guid id) =>
            Task.FromResult<Project?>(new Project { Id = id, Workspace = _workspace });

        public Task<IReadOnlyList<Project>> ListAsync(Expression<Func<Project, bool>>? predicate = null) =>
            throw new NotSupportedException();

        public Task<string?> GetProjectExtraSettingAsync(Guid? projectId) =>
            throw new NotSupportedException();

        public Task<Guid?> ResolveProjectIdAsync(Guid? projectId) =>
            throw new NotSupportedException();

        public Task<Project?> CreateAsync(Project project, string user) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id) =>
            throw new NotSupportedException();

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

    private sealed class CapturingSink : IExecutionMessageSink
    {
        public List<AgwMessage> Messages { get; } = [];

        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return ValueTask.CompletedTask;
        }
    }
}
