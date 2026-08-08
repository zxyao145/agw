using System.Linq.Expressions;

using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Commands.Interrupt;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using Agw.Files.Utils;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Projects;

namespace Agw.Agents.Tests;

public class ExecutionCommandHandlerTests
{
    [Fact]
    public async Task SettingCommand_ChangedSettings_ReleasesRuntimeAndClearsResolvedState()
    {
        var task = CreateTask("old");
        var runtimeFactory = new FakeRuntimeFactory();
        var context = CreateContext(runtimeFactory, task);
        var handler = new SettingCommandHandler();
        await handler.HandleAsync(
            new SettingCommand(task.ProjectId, contextId: "old"),
            context,
            TestContext.Current.CancellationToken);
        await context.StartTurnAsync(
            CreateExecCommand(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);
        var runtime = Assert.IsType<TestRuntime>(runtimeFactory.StartRequests[0].CurrentRuntime
            ?? runtimeFactory.CreatedRuntimes[0]);

        await handler.HandleAsync(
            new SettingCommand(Guid.CreateVersion7(), contextId: "new"),
            context,
            TestContext.Current.CancellationToken);

        Assert.True(runtime.Disposed);
        Assert.Null(context.ResolvedTask);
        Assert.Null(context.Workspace);
        Assert.Null(context.Target);
        Assert.Equal("new", context.Settings!.ContextId);
        await context.DisposeAsync();
    }

    [Fact]
    public async Task SettingCommand_ActiveTurn_SendsBusyWithoutChangingSettings()
    {
        var sink = new CapturingSink();
        var task = CreateTask("current");
        var runtimeFactory = new FakeRuntimeFactory { HoldTurnOpen = true };
        var context = CreateContext(runtimeFactory, task, sink: sink);
        var current = new SettingCommand(task.ProjectId, contextId: "current");
        await context.ApplySettingsAsync(
            ExecutionSettings.FromCommand(current),
            TestContext.Current.CancellationToken);
        await context.StartTurnAsync(
            CreateExecCommand(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        await new SettingCommandHandler().HandleAsync(
            new SettingCommand(Guid.CreateVersion7(), contextId: "new"),
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal("current", context.Settings!.ContextId);
        Assert.IsType<AgwErrorContent>(Assert.Single(sink.Messages).Contents[0]);

        runtimeFactory.CompleteHeldTurn();
        await runtimeFactory.CreatedRuntimes[0].WhenIdleAsync();
        await context.DisposeAsync();
    }

    [Fact]
    public async Task InterruptCommand_WithoutActiveTurn_SendsExistingSystemMessage()
    {
        var sink = new CapturingSink();
        await using var context = CreateContext(new FakeRuntimeFactory(), CreateTask("unused"), sink: sink);

        await new InterruptCommandHandler().HandleAsync(
            new InterruptCommand { Reason = "nothing running" },
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "nothing running",
            Assert.IsType<AgwTextContent>(Assert.Single(sink.Messages).Contents[0]).Content);
    }

    [Fact]
    public async Task InterruptCommand_WithActiveTurn_ForwardsCancellation()
    {
        var runtimeFactory = new FakeRuntimeFactory { HoldTurnOpen = true };
        await using var context = CreateContext(runtimeFactory, CreateTask("active"));
        await context.StartTurnAsync(
            CreateExecCommand(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        await new InterruptCommandHandler().HandleAsync(
            new InterruptCommand { Reason = "stop" },
            context,
            TestContext.Current.CancellationToken);

        Assert.True(runtimeFactory.HeldTurnCancellation!.IsCancellationRequested);
        runtimeFactory.CompleteHeldTurn();
        await runtimeFactory.CreatedRuntimes[0].WhenIdleAsync();
    }

    [Fact]
    public async Task HumanResponseCommand_WithoutPendingGate_SendsExistingSystemMessage()
    {
        var sink = new CapturingSink();
        await using var context = CreateContext(new FakeRuntimeFactory(), CreateTask("unused"), sink: sink);

        await new HumanResponseCommandHandler().HandleAsync(
            new HumanResponseCommand("missing", approved: true),
            context,
            TestContext.Current.CancellationToken);

        var content = Assert.IsType<AgwTextContent>(Assert.Single(sink.Messages).Contents[0]);
        Assert.Equal("No matching HumanGate request is waiting for this response.", content.Content);
    }

    [Fact]
    public async Task ExecCommand_ReusesResolvedTaskWorkspaceAndRuntimeForSameTarget()
    {
        var task = CreateTask("resolved");
        var taskAppService = new FakeTaskAppService(task);
        var projectAppService = new FakeProjectAppService("~/.agw/temp");
        var runtimeFactory = new FakeRuntimeFactory();
        await using var context = CreateContext(
            runtimeFactory,
            task,
            taskAppService: taskAppService,
            projectAppService: projectAppService);
        var command = CreateExecCommand(Guid.CreateVersion7());
        var handler = new ExecCommandHandler();

        await handler.HandleAsync(command, context, TestContext.Current.CancellationToken);
        await handler.HandleAsync(command, context, TestContext.Current.CancellationToken);

        Assert.Equal(1, taskAppService.ResolveCount);
        Assert.Equal(1, projectAppService.GetCount);
        Assert.Equal(2, runtimeFactory.StartRequests.Count);
        Assert.Null(runtimeFactory.StartRequests[0].CurrentRuntime);
        Assert.Same(
            runtimeFactory.CreatedRuntimes[0],
            runtimeFactory.StartRequests[1].CurrentRuntime);
    }

    [Fact]
    public async Task ExecCommand_ChangedTarget_ReleasesPreviousRuntime()
    {
        var runtimeFactory = new FakeRuntimeFactory();
        await using var context = CreateContext(runtimeFactory, CreateTask("resolved"));
        var handler = new ExecCommandHandler();

        await handler.HandleAsync(
            CreateExecCommand(Guid.CreateVersion7()),
            context,
            TestContext.Current.CancellationToken);
        var previous = runtimeFactory.CreatedRuntimes[0];
        await handler.HandleAsync(
            CreateExecCommand(Guid.CreateVersion7()),
            context,
            TestContext.Current.CancellationToken);

        Assert.True(previous.Disposed);
        Assert.Null(runtimeFactory.StartRequests[1].CurrentRuntime);
    }

    [Fact]
    public async Task ExecCommand_ProjectWorkspaceWithTilde_AddsExpandedAbsoluteWorkspaceToTurnContext()
    {
        var runtimeFactory = new FakeRuntimeFactory();
        const string configuredWorkspace = "~/.agw/runtime-context-test";
        await using var context = CreateContext(
            runtimeFactory,
            CreateTask("resolved"),
            projectAppService: new FakeProjectAppService(configuredWorkspace));

        await new ExecCommandHandler().HandleAsync(
            CreateExecCommand(Guid.CreateVersion7()),
            context,
            TestContext.Current.CancellationToken);

        var expectedWorkspace = Path.GetFullPath(PathUtil.ExpandTilde(configuredWorkspace));
        var turnContext = Assert.Single(runtimeFactory.StartRequests).TurnContext;
        Assert.Equal(expectedWorkspace, turnContext.Workspace);
        Assert.Equal(turnContext.Task.ProjectId, turnContext.ProjectId);
        Assert.Equal(turnContext.Target.AgentId, turnContext.AgentId);
        Assert.Equal(turnContext.ProjectId, context.ProjectId);
        Assert.Equal(turnContext.ProjectContextId, context.ProjectContextId);
        Assert.Equal(turnContext.AgentId, context.AgentId);
        Assert.Equal("user", context.UserName);
    }

    private static ExecutionConnectionContext CreateContext(
        IRuntimeFactory runtimeFactory,
        TaskProjection task,
        IExecutionMessageSink? sink = null,
        ITaskAppService? taskAppService = null,
        IProjectAppService? projectAppService = null) =>
        new(
            "user",
            sink ?? new CapturingSink(),
            CancellationToken.None,
            runtimeFactory,
            taskAppService ?? new FakeTaskAppService(task),
            projectAppService ?? new FakeProjectAppService("~/.agw/temp"));

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
            ProjectContextId = Guid.CreateVersion7(),
            ProjectId = Guid.CreateVersion7(),
            ContextId = contextId,
            Title = "test",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private sealed class FakeRuntimeFactory : IRuntimeFactory
    {
        private TaskCompletionSource? _heldTurnCompletion;

        public bool HoldTurnOpen { get; init; }

        public CancellationTokenSource? HeldTurnCancellation { get; private set; }

        public List<RuntimeStartRequest> StartRequests { get; } = [];

        public List<TestRuntime> CreatedRuntimes { get; } = [];

        public Task<RuntimeStartResult> StartAsync(
            RuntimeStartRequest request,
            CancellationToken cancellationToken)
        {
            StartRequests.Add(request);
            var runtime = request.CurrentRuntime as TestRuntime;
            if (runtime == null)
            {
                runtime = new TestRuntime();
                CreatedRuntimes.Add(runtime);
            }

            if (!HoldTurnOpen)
            {
                return Task.FromResult(new RuntimeStartResult(
                    runtime,
                    new ActiveTurn(Task.CompletedTask, new CancellationTokenSource())));
            }

            _heldTurnCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            HeldTurnCancellation = new CancellationTokenSource();
            var activeTurn = new ActiveTurn(_heldTurnCompletion.Task, HeldTurnCancellation);
            runtime.TryStartTurn(activeTurn);
            return Task.FromResult(new RuntimeStartResult(runtime, activeTurn));
        }

        public void CompleteHeldTurn() => _heldTurnCompletion!.TrySetResult();
    }

    private sealed class FakeTaskAppService : ITaskAppService
    {
        private readonly TaskProjection _task;

        public FakeTaskAppService(TaskProjection task)
        {
            _task = task;
        }

        public int ResolveCount { get; private set; }

        public Task<ExecutionTaskResolutionResult> ResolveTaskAsync(
            ExecutionTaskRequest request,
            CancellationToken cancellationToken)
        {
            ResolveCount++;
            return Task.FromResult(new ExecutionTaskResolutionResult(_task, null));
        }

        public Task<TaskProjection?> GetTaskAsync(Guid value) => throw new NotSupportedException();

        public Task<TaskProjection?> CreateTaskForExecutionAsync(
            Guid projectId,
            Guid? taskId,
            string input,
            string user,
            string? contextId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> HasTaskAsync(
            Guid taskId,
            Guid? projectId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProjectAppService : IProjectAppService
    {
        private readonly string _workspace;

        public FakeProjectAppService(string workspace)
        {
            _workspace = workspace;
        }

        public int GetCount { get; private set; }

        public Task<Project?> GetAsync(Guid id)
        {
            GetCount++;
            return Task.FromResult<Project?>(new Project { Id = id, Workspace = _workspace });
        }

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
