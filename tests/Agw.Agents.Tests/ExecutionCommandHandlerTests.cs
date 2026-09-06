using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Commands.Interrupt;
using Agw.Agents.Execution.Commands.Mode;
using Agw.Agents.Execution.Commands.Permission;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Durable;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using Agw.Files.Utils;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public partial class ExecutionCommandHandlerTests
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
            TestContext.Current.CancellationToken
        );
        await context.StartTurnAsync(CreateExecCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken);
        var runtime = Assert.IsType<TestRuntime>(
            runtimeFactory.StartRequests[0].CurrentRuntime ?? runtimeFactory.CreatedRuntimes[0]
        );

        await handler.HandleAsync(
            new SettingCommand(Guid.CreateVersion7(), contextId: "new"),
            context,
            TestContext.Current.CancellationToken
        );

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
        await context.ApplySettingsAsync(ExecutionSettings.FromCommand(current), TestContext.Current.CancellationToken);
        await context.StartTurnAsync(CreateExecCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        await new SettingCommandHandler().HandleAsync(
            new SettingCommand(Guid.CreateVersion7(), contextId: "new"),
            context,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("current", context.Settings!.ContextId);
        Assert.IsType<AgwErrorContent>(Assert.Single(sink.Messages).Contents[0]);

        runtimeFactory.CompleteHeldTurn();
        await runtimeFactory.CreatedRuntimes[0].WhenIdleAsync();
        await context.DisposeAsync();
    }

    [Fact]
    public async Task SettingCommand_ActiveTurnWithUnchangedSettings_DoesNotSendBusy()
    {
        var sink = new CapturingSink();
        var task = CreateTask("current");
        var runtimeFactory = new FakeRuntimeFactory { HoldTurnOpen = true };
        var context = CreateContext(runtimeFactory, task, sink: sink);
        var handler = new SettingCommandHandler();
        await handler.HandleAsync(
            new SettingCommand(task.ProjectId, contextId: "current"),
            context,
            TestContext.Current.CancellationToken
        );
        await context.StartTurnAsync(CreateExecCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        await handler.HandleAsync(
            new SettingCommand(task.ProjectId, contextId: "current"),
            context,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(sink.Messages);

        runtimeFactory.CompleteHeldTurn();
        await runtimeFactory.CreatedRuntimes[0].WhenIdleAsync();
        await context.DisposeAsync();
    }

    [Fact]
    public async Task InterruptCommand_WithoutActiveTurn_SendsSystemMessageAndInterruptedFinish()
    {
        var sink = new CapturingSink();
        await using var context = CreateContext(new FakeRuntimeFactory(), CreateTask("unused"), sink: sink);

        await new InterruptCommandHandler().HandleAsync(
            new InterruptCommand { Reason = "nothing running" },
            context,
            TestContext.Current.CancellationToken
        );

        Assert.Collection(
            sink.Messages,
            message =>
                Assert.Equal("nothing running", Assert.IsType<AgwTextContent>(Assert.Single(message.Contents)).Content),
            message =>
            {
                Assert.Equal("turn-finished", message.AdditionalProperties!["type"]);
                Assert.Equal("interrupted", message.AdditionalProperties["status"]);
            }
        );
    }

    [Fact]
    public async Task DurableSession_InterruptWithoutActiveExecution_SendsInterruptedFinish()
    {
        var sink = new CapturingSink();
        await using var session = new DurableExecutionSession(
            "user-id",
            sink,
            CancellationToken.None,
            coordinator: null!
        );

        await session.InterruptAsync(executionId: null, "nothing running", TestContext.Current.CancellationToken);

        Assert.Collection(
            sink.Messages,
            message =>
                Assert.Equal("nothing running", Assert.IsType<AgwTextContent>(Assert.Single(message.Contents)).Content),
            message =>
            {
                Assert.Equal("turn-finished", message.AdditionalProperties!["type"]);
                Assert.Equal("interrupted", message.AdditionalProperties["status"]);
            }
        );
    }

    [Fact]
    public async Task InterruptCommand_WithActiveTurn_ForwardsCancellation()
    {
        var runtimeFactory = new FakeRuntimeFactory { HoldTurnOpen = true };
        await using var context = CreateContext(runtimeFactory, CreateTask("active"));
        await context.StartTurnAsync(CreateExecCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        await new InterruptCommandHandler().HandleAsync(
            new InterruptCommand { Reason = "stop" },
            context,
            TestContext.Current.CancellationToken
        );

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
            TestContext.Current.CancellationToken
        );

        var content = Assert.IsType<AgwTextContent>(Assert.Single(sink.Messages).Contents[0]);
        Assert.Equal("No matching HumanGate request is waiting for this response.", content.Content);
    }

    [Fact]
    public async Task SetModeCommand_BeforeRuntime_IsAppliedBeforeFirstTurn()
    {
        var sink = new CapturingSink();
        var runtimeFactory = new FakeRuntimeFactory();
        var agentId = Guid.CreateVersion7();
        await using var context = CreateContext(runtimeFactory, CreateTask("mode-context"), sink: sink);

        await new SetModeCommandHandler().HandleAsync(
            new SetModeCommand { AgentId = agentId, Mode = "execute" },
            context,
            TestContext.Current.CancellationToken
        );
        await context.StartTurnAsync(CreateExecCommand(agentId), TestContext.Current.CancellationToken);

        Assert.Equal("execute", Assert.Single(runtimeFactory.StartRequests).RequestedMode);
        var status = Assert.Single(sink.Messages);
        Assert.Equal("mode-status", status.AdditionalProperties?["type"]?.ToString());
        Assert.Equal("execute", status.AdditionalProperties?["mode"]?.ToString());
    }

    [Fact]
    public async Task SetModeCommand_DuringActiveTurn_AppliesLatestModeAfterTurnFinishes()
    {
        var sink = new CapturingSink();
        var runtimeFactory = new ModeTestRuntimeFactory();
        var agentId = Guid.CreateVersion7();
        await using var context = CreateContext(runtimeFactory, CreateTask("mode-context"), sink: sink);
        await context.StartTurnAsync(CreateExecCommand(agentId), TestContext.Current.CancellationToken);
        var handler = new SetModeCommandHandler();

        await handler.HandleAsync(
            new SetModeCommand { AgentId = agentId, Mode = "plan" },
            context,
            TestContext.Current.CancellationToken
        );
        await handler.HandleAsync(
            new SetModeCommand { AgentId = agentId, Mode = "execute" },
            context,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(runtimeFactory.ModeChanges);
        runtimeFactory.CompleteHeldTurn();
        await runtimeFactory.Runtime.WhenIdleAsync();

        Assert.Equal(["execute"], runtimeFactory.ModeChanges);
        var status = Assert.Single(sink.Messages);
        Assert.Equal("mode-status", status.AdditionalProperties?["type"]?.ToString());
        Assert.Equal("execute", status.AdditionalProperties?["mode"]?.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    public async Task SetModeCommand_InvalidMode_ThrowsInvalidParam(string mode)
    {
        await using var context = CreateContext(new FakeRuntimeFactory(), CreateTask("mode-context"));

        var exception = await Assert.ThrowsAsync<Agw.Shared.Exceptions.AgwException>(() =>
            new SetModeCommandHandler().HandleAsync(
                new SetModeCommand { AgentId = Guid.CreateVersion7(), Mode = mode },
                context,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(Agw.Shared.Exceptions.ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task SetPermissionModeCommand_BeforeRuntime_UpdatesSettings()
    {
        await using var context = CreateContext(new FakeRuntimeFactory(), CreateTask("permission-context"));

        await new SetPermissionModeCommandHandler().HandleAsync(
            new SetPermissionModeCommand { PermissionMode = PermissionMode.AllowSameArguments },
            context,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(PermissionMode.AllowSameArguments, context.Settings!.PermissionMode);
    }

    [Fact]
    public async Task SetPermissionModeCommand_DuringActiveTurn_AppliesImmediately()
    {
        var runtimeFactory = new PermissionTestRuntimeFactory();
        await using var context = CreateContext(runtimeFactory, CreateTask("permission-context"));
        await context.StartTurnAsync(CreateExecCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        await new SetPermissionModeCommandHandler().HandleAsync(
            new SetPermissionModeCommand { PermissionMode = PermissionMode.FullAccess },
            context,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(PermissionMode.FullAccess, context.Settings!.PermissionMode);
        Assert.Equal([PermissionMode.FullAccess], runtimeFactory.ActiveChanges);
        Assert.Equal([PermissionMode.FullAccess], runtimeFactory.RuntimeChanges);

        runtimeFactory.CompleteHeldTurn();
        await runtimeFactory.Runtime.WhenIdleAsync();
    }

    [Fact]
    public async Task SetPermissionModeCommand_MissingMode_ThrowsInvalidParam()
    {
        await using var context = CreateContext(new FakeRuntimeFactory(), CreateTask("permission-context"));

        var exception = await Assert.ThrowsAsync<Agw.Shared.Exceptions.AgwException>(() =>
            new SetPermissionModeCommandHandler().HandleAsync(
                new SetPermissionModeCommand(),
                context,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(Agw.Shared.Exceptions.ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task ExecCommand_ReusesResolvedTaskWorkspaceAndRuntimeForSameTarget()
    {
        var task = CreateTask("resolved");
        var projectTasks = new FakeProjectTaskFacade(task);
        var projects = new FakeProjectRuntimeFacade("~/.agw/temp");
        var runtimeFactory = new FakeRuntimeFactory();
        await using var context = CreateContext(runtimeFactory, task, projectTasks: projectTasks, projects: projects);
        var command = CreateExecCommand(Guid.CreateVersion7());
        var handler = new ExecCommandHandler();

        await handler.HandleAsync(command, context, TestContext.Current.CancellationToken);
        await handler.HandleAsync(command, context, TestContext.Current.CancellationToken);

        Assert.Equal(1, projectTasks.ResolveCount);
        Assert.Equal(1, projects.GetCount);
        Assert.Equal(2, runtimeFactory.StartRequests.Count);
        Assert.Null(runtimeFactory.StartRequests[0].CurrentRuntime);
        Assert.Same(runtimeFactory.CreatedRuntimes[0], runtimeFactory.StartRequests[1].CurrentRuntime);
    }

    [Fact]
    public async Task ExecCommand_ChangedTarget_ReleasesPreviousRuntime()
    {
        var runtimeFactory = new FakeRuntimeFactory();
        await using var context = CreateContext(runtimeFactory, CreateTask("resolved"));
        var handler = new ExecCommandHandler();
        var firstCommand = CreateExecCommand(Guid.CreateVersion7());

        await handler.HandleAsync(firstCommand, context, TestContext.Current.CancellationToken);
        var previous = runtimeFactory.CreatedRuntimes[0];
        var secondCommand = CreateExecCommand(Guid.CreateVersion7());
        secondCommand.ConversationId = firstCommand.ConversationId;
        await handler.HandleAsync(secondCommand, context, TestContext.Current.CancellationToken);

        Assert.True(previous.Disposed);
        Assert.Null(runtimeFactory.StartRequests[1].CurrentRuntime);
    }

    [Fact]
    public async Task ExecCommand_ProjectWorkspaceWithTilde_AddsExpandedAbsoluteWorkspaceToTurnContext()
    {
        var runtimeFactory = new FakeRuntimeFactory();
        var task = CreateTask("resolved");
        var projectTasks = new FakeProjectTaskFacade(task);
        const string configuredWorkspace = "~/.agw/runtime-context-test";
        await using var context = CreateContext(
            runtimeFactory,
            task,
            projectTasks: projectTasks,
            projects: new FakeProjectRuntimeFacade(configuredWorkspace)
        );
        var command = CreateExecCommand(Guid.CreateVersion7());

        await new ExecCommandHandler().HandleAsync(command, context, TestContext.Current.CancellationToken);

        var expectedWorkspace = Path.GetFullPath(PathUtil.ExpandTilde(configuredWorkspace));
        var turnContext = Assert.Single(runtimeFactory.StartRequests).TurnContext;
        Assert.Equal(expectedWorkspace, turnContext.Workspace);
        Assert.Equal(turnContext.Task.ProjectId, turnContext.ProjectId);
        Assert.Equal(turnContext.Target.AgentId, turnContext.AgentId);
        Assert.Equal(turnContext.ProjectId, context.ProjectId);
        Assert.Equal(turnContext.ProjectConversationId, context.ProjectConversationId);
        Assert.Equal(turnContext.AgentId, context.AgentId);
        Assert.Equal("user-id", context.UserId);
        Assert.Equal("user-id", turnContext.UserId);
        Assert.Equal("user-id", projectTasks.LastRequest?.OwnerUserId);
        Assert.Equal(command.ConversationId, projectTasks.LastRequest?.ConversationId);
    }

    [Fact]
    public async Task ExecCommand_WithoutConversationId_DoesNotResolveTaskOrStartRuntime()
    {
        var task = CreateTask("resolved");
        var projectTasks = new FakeProjectTaskFacade(task);
        var runtimeFactory = new FakeRuntimeFactory();
        await using var context = CreateContext(runtimeFactory, task, projectTasks: projectTasks);
        var command = CreateExecCommand(Guid.CreateVersion7());
        command.ConversationId = null;

        var exception = await Assert.ThrowsAsync<Agw.Shared.Exceptions.AgwException>(() =>
            context.StartTurnAsync(command, TestContext.Current.CancellationToken)
        );

        Assert.Equal(Agw.Shared.Exceptions.ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Equal(0, projectTasks.ResolveCount);
        Assert.Empty(runtimeFactory.StartRequests);
    }

    [Fact]
    public async Task ExecCommand_WithEmptyConversationId_DoesNotResolveTaskOrStartRuntime()
    {
        var task = CreateTask("resolved");
        var projectTasks = new FakeProjectTaskFacade(task);
        var runtimeFactory = new FakeRuntimeFactory();
        await using var context = CreateContext(runtimeFactory, task, projectTasks: projectTasks);
        var command = CreateExecCommand(Guid.CreateVersion7());
        command.ConversationId = Guid.Empty;

        var exception = await Assert.ThrowsAsync<Agw.Shared.Exceptions.AgwException>(() =>
            context.StartTurnAsync(command, TestContext.Current.CancellationToken)
        );

        Assert.Equal(Agw.Shared.Exceptions.ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Equal(0, projectTasks.ResolveCount);
        Assert.Empty(runtimeFactory.StartRequests);
    }

    private static ExecutionConnectionContext CreateContext(
        IRuntimeFactory runtimeFactory,
        AgentExecutionTask task,
        IExecutionMessageSink? sink = null,
        IProjectTaskFacade? projectTasks = null,
        IProjectRuntimeFacade? projects = null
    ) =>
        new(
            "user-id",
            sink ?? new CapturingSink(),
            CancellationToken.None,
            runtimeFactory,
            projectTasks ?? new FakeProjectTaskFacade(task),
            projects ?? new FakeProjectRuntimeFacade("~/.agw/temp")
        );

    private static ExecCommand CreateExecCommand(Guid agentId) =>
        new(AgentRuntimeType.Agent, new AgwUserInput { Contents = [new AgwTextContent { Content = "hello" }] })
        {
            AgentId = agentId,
            ConversationId = Guid.CreateVersion7(),
        };

    private static AgentExecutionTask CreateTask(string contextId) =>
        new()
        {
            TaskId = Guid.CreateVersion7(),
            ProjectConversationId = Guid.CreateVersion7(),
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

        public Task<RuntimeStartResult> StartAsync(RuntimeStartRequest request, CancellationToken cancellationToken)
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
                return Task.FromResult(
                    new RuntimeStartResult(runtime, new ActiveTurn(Task.CompletedTask, new CancellationTokenSource()))
                );
            }

            _heldTurnCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            HeldTurnCancellation = new CancellationTokenSource();
            var activeTurn = new ActiveTurn(_heldTurnCompletion.Task, HeldTurnCancellation);
            runtime.TryStartTurn(activeTurn);
            return Task.FromResult(new RuntimeStartResult(runtime, activeTurn));
        }

        public void CompleteHeldTurn() => _heldTurnCompletion!.TrySetResult();
    }

    private sealed class FakeProjectTaskFacade : IProjectTaskFacade
    {
        public Task<int?> GetGenerationAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(Generation);

        public int? Generation { get; set; } = 0;

        private readonly ProjectTaskSnapshot _task;

        public FakeProjectTaskFacade(AgentExecutionTask task)
        {
            _task = ToSnapshot(task);
        }

        public int ResolveCount { get; private set; }

        public ResolveProjectTaskRequest? LastRequest { get; private set; }

        public Task<ProjectTaskSnapshot> ResolveAsync(
            ResolveProjectTaskRequest request,
            CancellationToken cancellationToken = default
        )
        {
            ResolveCount++;
            LastRequest = request;
            return Task.FromResult(
                _task with
                {
                    ProjectConversationId = request.ConversationId,
                    Generation = Generation ?? 0,
                }
            );
        }

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

    private sealed class ModeTestRuntimeFactory : IRuntimeFactory
    {
        private readonly TaskCompletionSource _turnCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModeTestRuntimeFactory()
        {
            Runtime = new AgentRuntime(
                NullLogger.Instance,
                new ModeTestAgent(),
                new ModeTestSession(),
                Guid.CreateVersion7(),
                "mode-context",
                sessionStateScope: null
            );
        }

        public AgentRuntime Runtime { get; }

        public List<string> ModeChanges { get; } = [];

        public Task<RuntimeStartResult> StartAsync(RuntimeStartRequest request, CancellationToken cancellationToken)
        {
            var activeTurn = new ActiveTurn(_turnCompletion.Task, new CancellationTokenSource());
            Runtime.TryStartTurn(activeTurn);
            return Task.FromResult(new RuntimeStartResult(Runtime, activeTurn));
        }

        public Task SetModeAsync(RuntimeBase runtime, string mode, CancellationToken cancellationToken)
        {
            Assert.Same(Runtime, runtime);
            ModeChanges.Add(mode);
            return Task.CompletedTask;
        }

        public void CompleteHeldTurn() => _turnCompletion.TrySetResult();
    }

    private sealed class PermissionTestRuntimeFactory : IRuntimeFactory
    {
        private readonly TaskCompletionSource _turnCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestRuntime Runtime { get; } = new();

        public List<PermissionMode> ActiveChanges { get; } = [];

        public List<PermissionMode> RuntimeChanges { get; } = [];

        public Task<RuntimeStartResult> StartAsync(RuntimeStartRequest request, CancellationToken cancellationToken)
        {
            var activeTurn = new ActiveTurn(
                _turnCompletion.Task,
                new CancellationTokenSource(),
                setPermissionModeAsync: (permissionMode, _) =>
                {
                    ActiveChanges.Add(permissionMode);
                    return ValueTask.CompletedTask;
                }
            );
            Runtime.TryStartTurn(activeTurn);
            return Task.FromResult(new RuntimeStartResult(Runtime, activeTurn));
        }

        public Task SetPermissionModeAsync(
            RuntimeBase runtime,
            PermissionMode permissionMode,
            CancellationToken cancellationToken
        )
        {
            Assert.Same(Runtime, runtime);
            RuntimeChanges.Add(permissionMode);
            return Task.CompletedTask;
        }

        public void CompleteHeldTurn() => _turnCompletion.TrySetResult();
    }

    private sealed class ModeTestAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new ModeTestSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new ModeTestSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class ModeTestSession : AgentSession;

    private sealed class FakeProjectRuntimeFacade : IProjectRuntimeFacade
    {
        private readonly string _workspace;

        public FakeProjectRuntimeFacade(string workspace)
        {
            _workspace = workspace;
        }

        public int GetCount { get; private set; }

        public Task<ProjectRuntimeSnapshot?> GetForCurrentUserAsync(
            Guid projectId,
            CancellationToken cancellationToken = default
        )
        {
            GetCount++;
            return Task.FromResult<ProjectRuntimeSnapshot?>(
                new ProjectRuntimeSnapshot(
                    projectId,
                    "project",
                    _workspace,
                    null,
                    [],
                    new Dictionary<string, string>(),
                    [],
                    [],
                    []
                )
            );
        }

        public Task<string?> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(_workspace);
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
