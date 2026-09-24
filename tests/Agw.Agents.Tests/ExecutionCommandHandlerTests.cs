using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Commands.Interrupt;
using Agw.Agents.Execution.Commands.Mode;
using Agw.Agents.Execution.Commands.Permission;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Utils;

namespace Agw.Agents.Tests;

public partial class ExecutionCommandHandlerTests : IAsyncLifetime
{
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(10);
    private readonly TestExecutionAgents _agents = new();
    private readonly InProcessCoordinatorTestKit _kit = new();
    private TurnPersistenceTestKit _persistence = null!;

    public async ValueTask InitializeAsync() => _persistence = await TurnPersistenceTestKit.CreateAsync();

    public async ValueTask DisposeAsync()
    {
        _agents.Dispose();
        await _persistence.DisposeAsync();
    }

    private TestAgentRuntimeFactory Runtimes => _kit.Runtimes;

    [Fact]
    public async Task SettingCommand_ChangedSettings_ReleasesRuntimeAndClearsResolvedState()
    {
        var task = CreateTask("old");
        var context = CreateContext(task);
        var handler = new SettingCommandHandler();
        await handler.HandleAsync(
            new SettingCommand(task.ProjectId, contextId: "old"),
            context,
            TestContext.Current.CancellationToken
        );
        await context.StartTurnAsync(CreateExecCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken);
        await context.WhenIdleAsync();
        var runtime = Assert.Single(Runtimes.Created);

        await handler.HandleAsync(
            new SettingCommand(Guid.CreateVersion7(), contextId: "new"),
            context,
            TestContext.Current.CancellationToken
        );

        Assert.True(runtime.IsDisposed);
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
        Runtimes.HoldTurnOpen = true;
        var context = CreateContext(task, sink: sink);
        var current = new SettingCommand(task.ProjectId, contextId: "current");
        await context.ApplySettingsAsync(
            SettingCommandMapper.FromCommand(current),
            TestContext.Current.CancellationToken
        );
        await context.StartTurnAsync(CreateExecCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken);
        await Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);

        await new SettingCommandHandler().HandleAsync(
            new SettingCommand(Guid.CreateVersion7(), contextId: "new"),
            context,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("current", context.Settings!.ContextId);
        Assert.IsType<AgwErrorContent>(
            Assert.Single(sink.Messages, message => message.Contents[0] is AgwErrorContent).Contents[0]
        );

        Runtimes.ReleaseHeldTurns();
        await context.WhenIdleAsync();
        await context.DisposeAsync();
    }

    [Fact]
    public async Task SettingCommand_ActiveTurnWithUnchangedSettings_DoesNotSendBusy()
    {
        var sink = new CapturingSink();
        var task = CreateTask("current");
        Runtimes.HoldTurnOpen = true;
        var context = CreateContext(task, sink: sink);
        var handler = new SettingCommandHandler();
        await handler.HandleAsync(
            new SettingCommand(task.ProjectId, contextId: "current"),
            context,
            TestContext.Current.CancellationToken
        );
        await context.StartTurnAsync(CreateExecCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken);
        await Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);

        await handler.HandleAsync(
            new SettingCommand(task.ProjectId, contextId: "current"),
            context,
            TestContext.Current.CancellationToken
        );

        Assert.DoesNotContain(sink.Messages, message => message.Contents.Any(content => content is AgwErrorContent));

        Runtimes.ReleaseHeldTurns();
        await context.WhenIdleAsync();
        await context.DisposeAsync();
    }

    [Fact]
    public async Task InterruptCommand_WithoutActiveTurn_SendsSystemMessageAndInterruptedFinish()
    {
        var sink = new CapturingSink();
        await using var context = CreateContext(CreateTask("unused"), sink: sink);

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
                Assert.Equal(AgwMessageTypes.TurnFinished, message.AdditionalProperties!["type"]);
                Assert.Equal("interrupted", message.AdditionalProperties["status"]);
            }
        );
    }

    [Fact]
    public async Task DurableAttachment_InterruptWithoutActiveExecution_SendsInterruptedFinish()
    {
        var sink = new CapturingSink();
        await using var attachment = new DurableExecutionAttachment(
            "user-id",
            sink,
            CancellationToken.None,
            coordinator: null!
        );

        await attachment.InterruptAsync(executionId: null, "nothing running", TestContext.Current.CancellationToken);

        Assert.Collection(
            sink.Messages,
            message =>
                Assert.Equal("nothing running", Assert.IsType<AgwTextContent>(Assert.Single(message.Contents)).Content),
            message =>
            {
                Assert.Equal(AgwMessageTypes.TurnFinished, message.AdditionalProperties!["type"]);
                Assert.Equal("interrupted", message.AdditionalProperties["status"]);
            }
        );
    }

    [Fact]
    public async Task InterruptCommand_WithActiveTurn_ForwardsCancellation()
    {
        var sink = new CapturingSink();
        Runtimes.HoldTurnOpen = true;
        await using var context = CreateContext(CreateTask("active"), sink: sink);
        await context.StartTurnAsync(CreateExecCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken);
        await Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);

        await new InterruptCommandHandler().HandleAsync(
            new InterruptCommand { Reason = "stop" },
            context,
            TestContext.Current.CancellationToken
        );
        await context.WhenIdleAsync();

        Assert.True(Assert.Single(Runtimes.Agents).Canceled);
        var finished = Assert.Single(sink.Messages, AgwMessageClassifier.IsTurnFinished);
        Assert.Equal("interrupted", finished.AdditionalProperties!["status"]);
    }

    [Fact]
    public async Task HumanResponseCommand_WithoutPendingGate_SendsExistingSystemMessage()
    {
        var sink = new CapturingSink();
        await using var context = CreateContext(CreateTask("unused"), sink: sink);

        await new HumanResponseCommandHandler().HandleAsync(
            new HumanResponseCommand(new WorkflowGateDecision { InteractionId = "missing", Approved = true }),
            context,
            TestContext.Current.CancellationToken
        );

        var content = Assert.IsType<AgwTextContent>(Assert.Single(sink.Messages).Contents[0]);
        Assert.Equal("No matching interaction is waiting for this response.", content.Content);
    }

    [Fact]
    public async Task SetModeCommand_BeforeRuntime_IsAppliedBeforeFirstTurn()
    {
        var sink = new CapturingSink();
        var agentId = Guid.CreateVersion7();
        await using var context = CreateContext(CreateTask("mode-context"), sink: sink);

        await new SetModeCommandHandler().HandleAsync(
            new SetModeCommand { AgentId = agentId, Mode = "execute" },
            context,
            TestContext.Current.CancellationToken
        );
        await context.StartTurnAsync(CreateExecCommand(agentId), TestContext.Current.CancellationToken);
        await context.WhenIdleAsync();

        Assert.Equal(["execute"], Runtimes.ModeChanges);
        var status = Assert.Single(
            sink.Messages,
            message => AgwMessageClassifier.GetMessageType(message) == AgwMessageTypes.ModeStatus
        );
        Assert.Equal("execute", status.AdditionalProperties?["mode"]?.ToString());
    }

    [Fact]
    public async Task SetModeCommand_DuringActiveTurn_AppliesLatestModeAfterTurnFinishes()
    {
        var sink = new CapturingSink();
        var agentId = Guid.CreateVersion7();
        Runtimes.HoldTurnOpen = true;
        await using var context = CreateContext(CreateTask("mode-context"), sink: sink);
        await context.StartTurnAsync(CreateExecCommand(agentId), TestContext.Current.CancellationToken);
        await Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
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

        Assert.Empty(Runtimes.ModeChanges);
        Runtimes.ReleaseHeldTurns();
        await context.WhenIdleAsync();

        Assert.Equal(["execute"], Runtimes.ModeChanges);
        var status = Assert.Single(
            sink.Messages,
            message => AgwMessageClassifier.GetMessageType(message) == AgwMessageTypes.ModeStatus
        );
        Assert.Equal("execute", status.AdditionalProperties?["mode"]?.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    public async Task SetModeCommand_InvalidMode_ThrowsInvalidParam(string mode)
    {
        await using var context = CreateContext(CreateTask("mode-context"));

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
        await using var context = CreateContext(CreateTask("permission-context"));

        await new SetPermissionModeCommandHandler().HandleAsync(
            new SetPermissionModeCommand { PermissionMode = AgwPermissionMode.AllowSameArguments },
            context,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(AgwPermissionMode.AllowSameArguments, context.Settings!.PermissionMode);
    }

    [Fact]
    public async Task SetPermissionModeCommand_DuringActiveTurn_OnlyUpdatesNextTurn()
    {
        Runtimes.HoldTurnOpen = true;
        await using var context = CreateContext(CreateTask("permission-context"));
        await context.StartTurnAsync(CreateExecCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken);
        await Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);

        await new SetPermissionModeCommandHandler().HandleAsync(
            new SetPermissionModeCommand { PermissionMode = AgwPermissionMode.FullAccess },
            context,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(AgwPermissionMode.FullAccess, context.Settings!.PermissionMode);
        var active = Assert.Single(Runtimes.TurnContexts);
        Assert.Null(active.PermissionMode);

        Runtimes.ReleaseHeldTurns();
        await context.WhenIdleAsync();
    }

    [Fact]
    public async Task PermissionChanges_ApplyOnNextTurnAndRevokeOnReturnToOriginalMode()
    {
        var sink = new CapturingSink();
        var task = CreateTask("permission-snapshot");
        Runtimes.HoldTurnOpen = true;
        await using var context = CreateContext(task, sink: sink);
        await context.ApplySettingsAsync(
            ExecutionSettings.CreateDefault().WithPermissionMode(AgwPermissionMode.AlwaysAsk),
            TestContext.Current.CancellationToken
        );
        var command = CreateExecCommand(Guid.NewGuid());
        await context.StartTurnAsync(command, TestContext.Current.CancellationToken);
        await Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        var original = Assert.Single(Runtimes.TurnContexts);
        await context.SetPermissionModeAsync(AgwPermissionMode.FullAccess, TestContext.Current.CancellationToken);
        await context.SetPermissionModeAsync(AgwPermissionMode.AlwaysAsk, TestContext.Current.CancellationToken);
        var status = sink.Messages.Last(message =>
            AgwMessageClassifier.GetMessageType(message) == AgwMessageTypes.PermissionStatus
        );
        Runtimes.HoldTurnOpen = false;
        Runtimes.ReleaseHeldTurns();
        await context.WhenIdleAsync();

        Assert.Equal(AgwPermissionMode.AlwaysAsk, original.PermissionMode);
        Assert.Equal(original.PermissionVersion + 2, context.Settings!.PermissionVersion);
        Assert.Equal(true, status.AdditionalProperties!["permissionChangePending"]);
        await context.StartTurnAsync(CreateNextCommand(command), TestContext.Current.CancellationToken);
        await context.WhenIdleAsync();
        Assert.Equal(context.Settings.PermissionVersion, Runtimes.TurnContexts[1].PermissionVersion);
    }

    [Fact]
    public async Task SetPermissionModeCommand_MissingMode_ThrowsInvalidParam()
    {
        await using var context = CreateContext(CreateTask("permission-context"));

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
    public async Task ExecCommand_ReusesTaskAndRuntimeButReloadsWorkspaceEachTurn()
    {
        var task = CreateTask("resolved");
        var projectTasks = new FakeProjectTaskFacade(task, _persistence);
        var projects = new FakeProjectRuntimeFacade(AppContext.BaseDirectory);
        await using var context = CreateContext(task, projectTasks: projectTasks, projects: projects);
        var command = CreateExecCommand(Guid.CreateVersion7());
        var handler = new ExecCommandHandler();

        await handler.HandleAsync(command, context, TestContext.Current.CancellationToken);
        await context.WhenIdleAsync();
        await handler.HandleAsync(CreateNextCommand(command), context, TestContext.Current.CancellationToken);
        await context.WhenIdleAsync();

        Assert.Equal(1, projectTasks.ResolveCount);
        Assert.Equal(2, projects.GetCount);
        Assert.Equal(2, Runtimes.TurnContexts.Count);
        Assert.Single(Runtimes.Created);
    }

    [Fact]
    public async Task ExecCommand_ChangedTarget_ReleasesPreviousRuntime()
    {
        await using var context = CreateContext(CreateTask("resolved"));
        var handler = new ExecCommandHandler();
        var firstCommand = CreateExecCommand(Guid.CreateVersion7());

        await handler.HandleAsync(firstCommand, context, TestContext.Current.CancellationToken);
        await context.WhenIdleAsync();
        var previous = Assert.Single(Runtimes.Created);
        var secondCommand = CreateExecCommand(Guid.CreateVersion7());
        secondCommand.ConversationId = firstCommand.ConversationId;
        await handler.HandleAsync(secondCommand, context, TestContext.Current.CancellationToken);
        await context.WhenIdleAsync();

        Assert.True(previous.IsDisposed);
        Assert.Equal(2, Runtimes.Created.Count);
    }

    [Fact]
    public async Task ExecCommand_ProjectWorkspaceWithTilde_AddsExpandedAbsoluteWorkspaceToExecutionContext()
    {
        var task = CreateTask("resolved");
        var projectTasks = new FakeProjectTaskFacade(task, _persistence);
        const string configuredWorkspace = "~";
        await using var context = CreateContext(
            task,
            projectTasks: projectTasks,
            projects: new FakeProjectRuntimeFacade(configuredWorkspace)
        );
        var command = CreateExecCommand(Guid.CreateVersion7());

        await new ExecCommandHandler().HandleAsync(command, context, TestContext.Current.CancellationToken);
        await context.WhenIdleAsync();

        var expectedWorkspace = Path.GetFullPath(PathUtil.ExpandTilde(configuredWorkspace));
        var execution = Assert.Single(Runtimes.TurnContexts);
        Assert.Equal(expectedWorkspace, execution.WorkspaceSnapshot.Workspace);
        Assert.Equal(task.ProjectId, execution.ProjectId);
        Assert.Equal(command.AgentId, execution.AgentId);
        Assert.Equal(command.AgentId, execution.TurnTargetId);
        Assert.Equal(command.ExecutionId, execution.TurnId);
        Assert.Equal(EngineKind.Maf, execution.EngineKind);
        Assert.Equal(ExecutionProvider.InProcess, execution.Provider);
        Assert.Equal(execution.ProjectId, context.ProjectId);
        Assert.Equal(execution.ProjectConversationId, context.ProjectConversationId);
        Assert.Equal(execution.AgentId, context.AgentId);
        Assert.Equal("user-id", context.UserId);
        Assert.Equal("user-id", execution.UserId);
        Assert.Equal("user-id", projectTasks.LastRequest?.OwnerUserId);
        Assert.Equal(command.ConversationId, projectTasks.LastRequest?.ConversationId);
    }

    [Fact]
    public async Task ExecCommand_WithoutConversationId_DoesNotResolveTaskOrStartRuntime()
    {
        var task = CreateTask("resolved");
        var projectTasks = new FakeProjectTaskFacade(task, _persistence);
        await using var context = CreateContext(task, projectTasks: projectTasks);
        var command = CreateExecCommand(Guid.CreateVersion7());
        command.ConversationId = null;

        var exception = await Assert.ThrowsAsync<Agw.Shared.Exceptions.AgwException>(() =>
            context.StartTurnAsync(command, TestContext.Current.CancellationToken)
        );

        Assert.Equal(Agw.Shared.Exceptions.ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Equal(0, projectTasks.ResolveCount);
        Assert.Empty(Runtimes.Created);
    }

    [Fact]
    public async Task ExecCommand_WithEmptyConversationId_DoesNotResolveTaskOrStartRuntime()
    {
        var task = CreateTask("resolved");
        var projectTasks = new FakeProjectTaskFacade(task, _persistence);
        await using var context = CreateContext(task, projectTasks: projectTasks);
        var command = CreateExecCommand(Guid.CreateVersion7());
        command.ConversationId = Guid.Empty;

        var exception = await Assert.ThrowsAsync<Agw.Shared.Exceptions.AgwException>(() =>
            context.StartTurnAsync(command, TestContext.Current.CancellationToken)
        );

        Assert.Equal(Agw.Shared.Exceptions.ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Equal(0, projectTasks.ResolveCount);
        Assert.Empty(Runtimes.Created);
    }

    private ExecutionConnectionContext CreateContext(
        AgentExecutionTask task,
        IExecutionMessageSink? sink = null,
        IProjectTaskFacade? projectTasks = null,
        IProjectRuntimeFacade? projects = null
    )
    {
        projectTasks ??= new FakeProjectTaskFacade(task, _persistence);
        return new(
            "user-id",
            sink ?? new CapturingSink(),
            CancellationToken.None,
            _persistence.CreateAcceptance(
                projectTasks,
                projects ?? new FakeProjectRuntimeFacade(AppContext.BaseDirectory)
            ),
            projectTasks,
            _kit.CreateFactory(_agents.ContextFactory, _persistence),
            durableCoordinator: null
        );
    }

    private ExecCommand CreateExecCommand(Guid agentId) =>
        new(AgentRuntimeType.Agent, new AgwUserInput { Contents = [new AgwTextContent { Content = "hello" }] })
        {
            AgentId = _agents.Add(agentId),
            ConversationId = Guid.CreateVersion7(),
        };

    /// <summary>
    /// 同一对话的下一条命令：客户端每个 Turn 发送新的 ExecCommand，不带 turnId 时由服务端分配。
    /// The next command of the same conversation: clients send a new ExecCommand per turn, and the server assigns the turnId when it is absent.
    /// </summary>
    private static ExecCommand CreateNextCommand(ExecCommand previous) =>
        new(previous.AgentType, new AgwUserInput { Contents = [new AgwTextContent { Content = "hello" }] })
        {
            AgentId = previous.AgentId,
            ConversationId = previous.ConversationId,
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

    private sealed class FakeProjectTaskFacade : IProjectTaskFacade
    {
        public Task<int?> GetGenerationAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(Generation);

        public int? Generation { get; set; } = 0;

        private readonly ProjectTaskSnapshot _task;
        private readonly TurnPersistenceTestKit _persistence;

        public FakeProjectTaskFacade(AgentExecutionTask task, TurnPersistenceTestKit persistence)
        {
            _task = ToSnapshot(task);
            _persistence = persistence;
        }

        public int ResolveCount { get; private set; }

        public ResolveProjectTaskRequest? LastRequest { get; private set; }

        /// <summary>
        /// 解析出的任务属于请求的对话；对话行与它的 Generation 写入测试数据库，受理事务按真实规则校验它。
        /// The resolved task belongs to the requested conversation; the conversation row and its generation are written to the test database so the acceptance transaction checks it by the real rules.
        /// </summary>
        public async Task<ProjectTaskSnapshot> ResolveAsync(
            ResolveProjectTaskRequest request,
            CancellationToken cancellationToken = default
        )
        {
            ResolveCount++;
            LastRequest = request;
            var resolved = _task with { ProjectConversationId = request.ConversationId, Generation = Generation ?? 0 };
            await _persistence.SeedConversationAsync(
                new AgentExecutionTask
                {
                    TaskId = resolved.TaskId,
                    ProjectId = resolved.ProjectId,
                    ProjectConversationId = resolved.ProjectConversationId,
                    ContextId = resolved.ContextId,
                    Generation = resolved.Generation,
                },
                request.OwnerUserId
            );
            await _persistence.SetGenerationAsync(resolved.ProjectConversationId, resolved.Generation);
            return resolved;
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

    private sealed class CapturingSink : IExecutionMessageSink
    {
        private readonly object _lock = new();
        private readonly List<AgwMessage> _messages = [];

        public List<AgwMessage> Messages
        {
            get
            {
                lock (_lock)
                {
                    return [.. _messages];
                }
            }
        }

        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _messages.Add(message);
            }
            return ValueTask.CompletedTask;
        }
    }
}
