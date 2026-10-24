using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Commands.Checkpoint;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

/// <summary>
/// 写入操作的检查：extension 只能写入自己创建的对话、开放对话与草稿对话；拒绝发生在解析任务、读取 checkpoint 与写出消息之前。
/// The check before writes: an extension may write only its own, open and draft conversations; the refusal happens before task resolution, checkpoint access and any message.
/// </summary>
public partial class ExecutionCommandHandlerTests
{
    [Fact]
    public async Task ApplySettingsAsync_ChangedExtensionId_ReleasesRuntimeAndClearsResolvedTask()
    {
        var token = TestContext.Current.CancellationToken;
        var task = CreateTask("extension-settings");
        await using var context = CreateContext(task);
        var command = CreateExecCommand(Guid.CreateVersion7());
        var conversationId = command.ConversationId!.Value;
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, conversationId, extensionId: null), token);
        await context.StartTurnAsync(command, token);
        await context.WhenIdleAsync();
        var runtime = Assert.Single(Runtimes.Created);

        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, conversationId, "sample"), token);

        Assert.True(runtime.IsDisposed);
        Assert.Null(context.ResolvedTask);
        Assert.Equal("sample", context.Settings!.ExtensionId);
    }

    [Fact]
    public async Task StartTurnAsync_ExtensionOnDraftConversation_ResolvesWithRequesterAndAcceptsNextTurn()
    {
        var token = TestContext.Current.CancellationToken;
        var task = CreateTask("draft-extension");
        await _persistence.SeedProjectAsync(task.ProjectId);
        await using var context = CreateContext(task, projectTasks: _persistence.CreateProjectTasks());
        var command = CreateExecCommand(Guid.CreateVersion7());
        var conversationId = command.ConversationId!.Value;
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, conversationId, "sample"), token);

        await context.StartTurnAsync(command, token);
        await context.WhenIdleAsync();
        await context.StartTurnAsync(CreateNextCommand(command), token);
        await context.WhenIdleAsync();

        Assert.Equal("sample", (await _persistence.ReadConversationAsync(conversationId)).ExtensionId);
        Assert.Equal(2, await _persistence.CountTurnsAsync(conversationId));
    }

    [Fact]
    public async Task StartTurnAsync_ExtensionOnHostConversationWithAcceptedTurn_ThrowsConversationReadOnlyBeforeResolving()
    {
        var token = TestContext.Current.CancellationToken;
        var task = CreateTask("host-conversation");
        await _persistence.SeedConversationAsync(task);
        await _persistence.AddTurnAsync(task.ProjectConversationId);
        var projectTasks = new FakeProjectTaskFacade(task, _persistence);
        await using var context = CreateContext(task, projectTasks: projectTasks);
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, task.ProjectConversationId, "sample"), token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.StartTurnAsync(CreateCommandFor(task), token)
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        Assert.Equal(0, projectTasks.ResolveCount);
        Assert.Empty(Runtimes.Created);
        Assert.Equal(1, await _persistence.CountTurnsAsync(task.ProjectConversationId));
    }

    [Fact]
    public async Task StartTurnAsync_ExtensionOnOtherExtensionConversation_ThrowsConversationReadOnly()
    {
        var token = TestContext.Current.CancellationToken;
        var task = CreateTask("other-extension");
        await _persistence.SeedConversationAsync(task, extensionId: "other");
        await using var context = CreateContext(task, projectTasks: _persistence.CreateProjectTasks());
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, task.ProjectConversationId, "sample"), token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.StartTurnAsync(CreateCommandFor(task), token)
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        Assert.Equal(0, await _persistence.CountTurnsAsync(task.ProjectConversationId));
        Assert.Equal("other", (await _persistence.ReadConversationAsync(task.ProjectConversationId)).ExtensionId);
    }

    [Fact]
    public async Task StartTurnAsync_ExtensionOnOpenConversation_AcceptsTurnAndRecordsCreator()
    {
        var token = TestContext.Current.CancellationToken;
        var task = CreateTask("open-conversation");
        await _persistence.SeedConversationAsync(task);
        await using var context = CreateContext(task, projectTasks: _persistence.CreateProjectTasks());
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, task.ProjectConversationId, "sample"), token);

        await context.StartTurnAsync(CreateCommandFor(task), token);
        await context.WhenIdleAsync();

        Assert.Equal("sample", (await _persistence.ReadConversationAsync(task.ProjectConversationId)).ExtensionId);
        Assert.Equal(1, await _persistence.CountTurnsAsync(task.ProjectConversationId));
    }

    [Fact]
    public async Task StartTurnAsync_HostOnExtensionConversation_AcceptsTurn()
    {
        var token = TestContext.Current.CancellationToken;
        var task = CreateTask("extension-conversation");
        await _persistence.SeedConversationAsync(task, extensionId: "sample");
        await _persistence.AddTurnAsync(task.ProjectConversationId);
        await using var context = CreateContext(task, projectTasks: _persistence.CreateProjectTasks());
        await context.ApplySettingsAsync(
            CreateSettings(task.ProjectId, task.ProjectConversationId, extensionId: null),
            token
        );

        await context.StartTurnAsync(CreateCommandFor(task), token);
        await context.WhenIdleAsync();

        Assert.Equal(2, await _persistence.CountTurnsAsync(task.ProjectConversationId));
        Assert.Equal("sample", (await _persistence.ReadConversationAsync(task.ProjectConversationId)).ExtensionId);
    }

    [Fact]
    public async Task StartTurnAsync_ConversationRecreatedByAnotherExtension_ThrowsConversationReadOnlyWithCachedTask()
    {
        var token = TestContext.Current.CancellationToken;
        var task = CreateTask("recreated-conversation");
        await _persistence.SeedProjectAsync(task.ProjectId);
        await using var context = CreateContext(task, projectTasks: _persistence.CreateProjectTasks());
        var command = CreateExecCommand(Guid.CreateVersion7());
        var conversationId = command.ConversationId!.Value;
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, conversationId, "sample"), token);
        await context.StartTurnAsync(command, token);
        await context.WhenIdleAsync();
        Assert.NotNull(context.ResolvedTask);
        // 另一个 extension 以同一 id 重新创建了对话：Generation 仍为 0，连接缓存的任务继续有效。
        // Another extension recreated the conversation with the same id: the generation is still 0, so the task the connection caches stays valid.
        await using (var db = _persistence.CreateContext())
        {
            await db
                .ProjectConversations.IgnoreQueryFilters()
                .Where(conversation => conversation.Id == conversationId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(conversation => conversation.ExtensionId, "other"),
                    token
                );
        }

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.StartTurnAsync(CreateNextCommand(command), token)
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        Assert.Equal(1, await _persistence.CountTurnsAsync(conversationId));
    }

    [Fact]
    public async Task ResumeCheckpointAsync_ReadOnlyRequester_ThrowsConversationReadOnlyBeforeCheckpointAccess()
    {
        var token = TestContext.Current.CancellationToken;
        var task = await SeedHostConversationWithTurnAsync("resume-read-only");
        await using var context = CreateContext(task);
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, task.ProjectConversationId, "sample"), token);

        // 没有配置 checkpoint 存储：检查之后的代码会以 InvalidParam 失败。
        // No checkpoint store is configured: code after the check would fail with InvalidParam.
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.ResumeCheckpointAsync(
                new ResumeCheckpointCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()),
                token
            )
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        Assert.Empty(Runtimes.TurnContexts);
    }

    [Fact]
    public async Task InterruptTurnAsync_ReadOnlyRequester_ThrowsConversationReadOnlyWithoutMessages()
    {
        var token = TestContext.Current.CancellationToken;
        var task = await SeedHostConversationWithTurnAsync("interrupt-read-only");
        var sink = new CapturingSink();
        await using var context = CreateContext(task, sink: sink);
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, task.ProjectConversationId, "sample"), token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.InterruptTurnAsync(turnId: null, reason: null, token)
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public async Task SubmitHumanDecisionAsync_ReadOnlyRequester_ThrowsConversationReadOnly()
    {
        var token = TestContext.Current.CancellationToken;
        var task = await SeedHostConversationWithTurnAsync("hitl-read-only");
        var sink = new CapturingSink();
        await using var context = CreateContext(task, sink: sink);
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, task.ProjectConversationId, "sample"), token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.SubmitHumanDecisionAsync(
                new HumanResponseCommand(new WorkflowGateDecision { InteractionId = "gate", Approved = true }),
                token
            )
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public async Task SetModeAsync_ReadOnlyRequester_ThrowsConversationReadOnly()
    {
        var token = TestContext.Current.CancellationToken;
        var task = await SeedHostConversationWithTurnAsync("mode-read-only");
        var sink = new CapturingSink();
        await using var context = CreateContext(task, sink: sink);
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, task.ProjectConversationId, "sample"), token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.SetModeAsync(Guid.CreateVersion7(), "plan", token)
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public async Task SetPermissionModeAsync_ReadOnlyRequester_ThrowsConversationReadOnlyAndKeepsSettings()
    {
        var token = TestContext.Current.CancellationToken;
        var task = await SeedHostConversationWithTurnAsync("permission-read-only");
        var sink = new CapturingSink();
        await using var context = CreateContext(task, sink: sink);
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, task.ProjectConversationId, "sample"), token);
        var settings = context.Settings;

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.SetPermissionModeAsync(AgwPermissionMode.FullAccess, token)
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        Assert.Same(settings, context.Settings);
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public async Task GetAgentflowCheckpointsAsync_ReadOnlyRequester_ReturnsCheckpoints()
    {
        var token = TestContext.Current.CancellationToken;
        var task = await SeedHostConversationWithTurnAsync("checkpoints-read-only");
        await using var context = CreateContext(
            task,
            projectTasks: _persistence.CreateProjectTasks(),
            checkpointStore: new AgentflowCheckpointStore(
                _persistence.Services.GetRequiredService<IServiceScopeFactory>(),
                _persistence.Locks,
                TimeProvider.System
            )
        );
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, task.ProjectConversationId, "sample"), token);

        var checkpoints = await context.GetAgentflowCheckpointsAsync(Guid.CreateVersion7(), token);

        Assert.Empty(checkpoints);
    }

    [Fact]
    public async Task InterruptTurnAsync_ReadOnlyRequesterAttachedToDurableTurn_ThrowsConversationReadOnlyAndKeepsTurnRunning()
    {
        var token = TestContext.Current.CancellationToken;
        var (task, turnId) = await AcceptHostDurableTurnAsync();
        await using var context = CreateDurableContext(new CapturingSink());
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, task.ProjectConversationId, "sample"), token);
        await context.SubscribeTurnAsync(turnId, cursor: null, token);
        Assert.True(context.HasActiveTurn);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.InterruptTurnAsync(turnId, reason: null, token)
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        using var user = TurnPersistenceTestKit.EnterUser();
        var status = await _persistence.Coordinator.GetStatusAsync(turnId, TurnPersistenceTestKit.UserId, token);
        Assert.False(DurableExecutionQueries.IsTerminal(status.Status));
    }

    [Fact]
    public async Task SubmitHumanDecisionAsync_ReadOnlyRequesterOnDurableTurn_ThrowsConversationReadOnly()
    {
        var token = TestContext.Current.CancellationToken;
        var (task, turnId) = await AcceptHostDurableTurnAsync();
        await using var context = CreateDurableContext(new CapturingSink());
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, task.ProjectConversationId, "sample"), token);
        await context.SubscribeTurnAsync(turnId, cursor: null, token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.SubmitHumanDecisionAsync(
                new HumanResponseCommand(new WorkflowGateDecision { InteractionId = "gate", Approved = true }, turnId),
                token
            )
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
    }

    [Fact]
    public async Task SetPermissionModeAsync_ReadOnlyRequesterOnDurableTurn_ThrowsConversationReadOnlyAndKeepsSettings()
    {
        var token = TestContext.Current.CancellationToken;
        var (task, turnId) = await AcceptHostDurableTurnAsync();
        await using var context = CreateDurableContext(new CapturingSink());
        await context.ApplySettingsAsync(CreateSettings(task.ProjectId, task.ProjectConversationId, "sample"), token);
        await context.SubscribeTurnAsync(turnId, cursor: null, token);
        var settings = context.Settings;

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.SetPermissionModeAsync(AgwPermissionMode.FullAccess, token)
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        Assert.Same(settings, context.Settings);
    }

    private static ExecutionSettings CreateSettings(Guid projectId, Guid conversationId, string? extensionId) =>
        SettingCommandMapper.FromCommand(new SettingCommand(projectId, conversationId, extensionId: extensionId));

    private ExecCommand CreateCommandFor(AgentExecutionTask task)
    {
        var command = CreateExecCommand(Guid.CreateVersion7());
        command.ConversationId = task.ProjectConversationId;
        return command;
    }

    /// <summary>
    /// 宿主创建、已有受理 Turn 的对话：extension 只能读取。
    /// A host-created conversation with an accepted turn: extensions may only read it.
    /// </summary>
    private async Task<AgentExecutionTask> SeedHostConversationWithTurnAsync(string contextId)
    {
        var task = CreateTask(contextId);
        await _persistence.SeedConversationAsync(task);
        await _persistence.AddTurnAsync(task.ProjectConversationId);
        return task;
    }

    /// <summary>
    /// 宿主在 Durable 模式下受理一个 Turn；没有 worker 执行它，Turn 保持未结束。
    /// The host accepts a durable turn; no worker runs it, so the turn stays unfinished.
    /// </summary>
    private async Task<(AgentExecutionTask Task, Guid TurnId)> AcceptHostDurableTurnAsync()
    {
        using var user = TurnPersistenceTestKit.EnterUser();
        var task = await _persistence.SeedConversationAsync();
        var accepted = await _persistence
            .CreateAcceptance(
                projectTasks: null,
                new FakeProjectRuntimeFacade(AppContext.BaseDirectory),
                _persistence.Coordinator
            )
            .AcceptAsync(
                new TurnAcceptanceRequest(
                    TurnPersistenceTestKit.UserId,
                    null,
                    new ExecutionTarget(Guid.CreateVersion7(), AgentRuntimeType.Agent),
                    task.ProjectConversationId,
                    TurnPersistenceTestKit.CreateInput("hello"),
                    TurnPersistenceTestKit.CreateSettings(task.ProjectId, task.ProjectConversationId),
                    Stream: true
                )
                {
                    Task = task,
                },
                TestContext.Current.CancellationToken
            );
        return (task, accepted.Request.TurnId);
    }

    private ExecutionConnectionContext CreateDurableContext(IExecutionMessageSink sink)
    {
        IProjectTaskFacade projectTasks = _persistence.CreateProjectTasks();
        return new(
            TurnPersistenceTestKit.UserId,
            sink,
            CancellationToken.None,
            _persistence.CreateAcceptance(
                projectTasks,
                new FakeProjectRuntimeFacade(AppContext.BaseDirectory),
                _persistence.Coordinator
            ),
            projectTasks,
            _persistence.CreateConversationAccess(),
            inProcessCoordinators: null,
            durableCoordinator: _persistence.Coordinator
        );
    }
}
