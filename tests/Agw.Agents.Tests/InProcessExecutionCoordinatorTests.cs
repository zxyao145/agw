using System.Security.Claims;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Data.Entities.Projects;

namespace Agw.Agents.Tests;

public partial class ExecutionCommandHandlerTests
{
    [Fact]
    public async Task StartAsync_InProcess_AcceptsBeforeExecutionCompletes()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        Runtimes.HoldTurnOpen = true;
        await using var coordinator = _kit.CreateFactory(_agents.ContextFactory, _persistence).Create(token);
        var task = CreateTask("starter");
        var targetId = _agents.Add(Guid.CreateVersion7());
        using var user = PushUser("user-id");
        var accepted = await AcceptAsync(task, targetId);

        // Act
        var receipt = await coordinator.StartAsync(accepted.Request, token).WaitAsync(TurnTimeout, token);
        await Runtimes.TurnStarts.WaitAsync(TurnTimeout, token);

        // Assert
        Assert.True(receipt.Accepted);
        Assert.Equal(accepted.Request.TurnId, receipt.TurnId);
        Assert.True(coordinator.Host!.HasActiveTurn);
        var context = Assert.Single(Runtimes.TurnContexts);
        Assert.Equal(AgentRuntimeType.Agent, context.RuntimeType);
        Assert.Equal(targetId, context.TurnTargetId);
        Assert.Equal(targetId, context.AgentId);
        Assert.Equal("user-id", context.UserId);
        Assert.Equal(accepted.Request.WorkspaceSnapshot, context.WorkspaceSnapshot);
        Assert.Equal(ProjectConversationTurnStatus.Running, (await _persistence.ReadTurnAsync(receipt.TurnId)).Status);

        Runtimes.ReleaseHeldTurns();
        await coordinator.Host.WhenIdleAsync();
        var finished = await _persistence.ReadTurnAsync(receipt.TurnId);
        Assert.Equal(ProjectConversationTurnStatus.Completed, finished.Status);
        Assert.NotNull(finished.FinishedAt);
    }

    [Fact]
    public async Task StartAsync_InProcessCanceledBeforeAcceptance_DoesNotCreateRuntime()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await using var coordinator = _kit.CreateFactory(_agents.ContextFactory, _persistence)
            .Create(CancellationToken.None);
        using var user = PushUser("user-id");
        var accepted = await AcceptAsync(CreateTask("starter"), Guid.CreateVersion7());

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.StartAsync(accepted.Request, cancellation.Token)
        );

        // Assert
        Assert.Null(coordinator.Host);
        Assert.Empty(Runtimes.Created);
    }

    [Fact]
    public async Task StartTurnAsync_UnavailableRuntime_ReportsFailedTurnWithoutTarget()
    {
        // Arrange
        var sink = new CapturingSink();
        Runtimes.CreatesNoRuntime = true;
        await using var context = CreateContext(CreateTask("starter"), sink: sink);

        // Act
        await context.StartTurnAsync(CreateExecCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        // Assert: the start message, the error and one failed finish, with the turn row failed.
        Assert.False(context.HasActiveTurn);
        Assert.Null(context.Target);
        var error = Assert.IsType<AgwErrorContent>(
            Assert.Single(
                Assert
                    .Single(sink.Messages, message => message.Contents.Any(content => content is AgwErrorContent))
                    .Contents
            )
        );
        Assert.Equal("Agent execution could not be started.", error.Content);
        var start = Assert.Single(sink.Messages, AgwMessageClassifier.IsTurnStart);
        var finished = Assert.Single(sink.Messages, AgwMessageClassifier.IsTurnFinished);
        Assert.Equal(AgwTurnStatus.Failed, finished.AdditionalProperties!["status"]);
        Assert.True(sink.Messages.IndexOf(start) < sink.Messages.IndexOf(finished));
        var turnId = Guid.Parse(start.AdditionalProperties!["turnId"]!.ToString()!);
        using var user = PushUser("user-id");
        var turn = await _persistence.ReadTurnAsync(turnId);
        Assert.Equal(ProjectConversationTurnStatus.Failed, turn.Status);
        Assert.Equal(finished.AdditionalProperties!["errorCode"], turn.ErrorCode);
    }

    [Fact]
    public async Task StartAsync_UnattendedApprovalRequest_FailsTurn()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        Runtimes.RequestsApproval = true;
        var sink = new CapturingSink();
        await using var coordinator = _kit.CreateFactory(_agents.ContextFactory, _persistence).Create(token);
        using var user = PushUser("user-id");
        var accepted = await AcceptAsync(
            CreateTask("unattended"),
            _agents.Add(Guid.CreateVersion7()),
            ExecutionSettings.CreateDefault().WithHumanInteractionPolicy(HumanInteractionPolicy.Reject),
            sink
        );

        // Act
        var receipt = await coordinator.StartAsync(accepted.Request, token);
        await coordinator.Host!.WhenIdleAsync();

        // Assert
        Assert.True(receipt.Accepted);
        var finished = Assert.Single(sink.Messages, AgwMessageClassifier.IsTurnFinished);
        Assert.Equal("failed", finished.AdditionalProperties!["status"]);
        Assert.DoesNotContain(sink.Messages, AgwMessageClassifier.IsInteractionRequest);
    }

    [Fact]
    public async Task StartAsync_InteractiveApprovalBatch_PublishesRequestAndContinuesAfterAnswer()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        Runtimes.RequestsApproval = true;
        var sink = new CapturingSink();
        await using var coordinator = _kit.CreateFactory(_agents.ContextFactory, _persistence)
            .Create(token, pendingCount => PendingCounts.Add(pendingCount));
        using var user = PushUser("user-id");
        var accepted = await AcceptAsync(CreateTask("interactive"), _agents.Add(Guid.CreateVersion7()), sink: sink);

        // Act
        await coordinator.StartAsync(accepted.Request, token);
        var published = await WaitForInteractionAsync(sink, token);
        var submitted = await coordinator.Host!.TrySubmitHumanResponseAsync(
            new ToolApprovalDecision
            {
                InteractionId = published.InteractionId,
                Approved = true,
                Scope = ApprovalScope.Once,
            },
            token
        );
        await coordinator.Host.WhenIdleAsync();

        // Assert
        Assert.True(submitted);
        Assert.IsType<ToolApprovalInteraction>(published);
        Assert.Equal(2, Runtimes.TurnContexts.Count);
        Assert.Equal([1, 0], PendingCounts);
        var finished = Assert.Single(sink.Messages, AgwMessageClassifier.IsTurnFinished);
        Assert.Equal("completed", finished.AdditionalProperties!["status"]);
        // 进程内输出按写出顺序连续编号，开始消息为 1。
        // In-process output is numbered contiguously in write order, with the start message at 1.
        Assert.True(AgwMessageClassifier.IsTurnStart(sink.Messages[0]));
        Assert.Equal(
            Enumerable.Range(1, sink.Messages.Count).Select(value => (long)value),
            sink.Messages.Select(message => (long)message.AdditionalProperties!["turnSequence"]!)
        );
        Assert.All(
            sink.Messages,
            message => Assert.Equal(accepted.Request.TurnId.ToString("D"), message.AdditionalProperties!["turnId"])
        );
    }

    private List<int> PendingCounts { get; } = [];

    private static async Task<InteractionRequest> WaitForInteractionAsync(
        CapturingSink sink,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TurnTimeout);
        while (true)
        {
            var message = sink.Messages.FirstOrDefault(AgwMessageClassifier.IsInteractionRequest);
            if (message != null)
                return InteractionTestData.Read(message);
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>
    /// 经真实的受理事务写入 Turn 行与输入行；给出 sink 时它订阅本 Turn 的广播。
    /// Writes the turn and input rows through the real acceptance transaction; a given sink subscribes to the turn's broadcast.
    /// </summary>
    private async Task<AcceptedTurn> AcceptAsync(
        AgentExecutionTask task,
        Guid targetId,
        ExecutionSettings? settings = null,
        IExecutionMessageSink? sink = null
    )
    {
        await _persistence.SeedConversationAsync(task);
        var accepted = await _persistence
            .CreateAcceptance(projectTasks: null, new FakeProjectRuntimeFacade(AppContext.BaseDirectory))
            .AcceptAsync(
                new TurnAcceptanceRequest(
                    "user-id",
                    TurnId: null,
                    new ExecutionTarget(targetId, AgentRuntimeType.Agent),
                    task.ProjectConversationId,
                    new AgwUserInput { Contents = [new AgwTextContent { Content = "hello" }] },
                    settings ?? ExecutionSettings.CreateDefault(),
                    Stream: true
                )
                {
                    Task = task,
                },
                TestContext.Current.CancellationToken
            );
        if (sink != null)
            accepted.Broadcast!.AddSink(sink);
        await accepted.Broadcast!.WriteAsync(accepted.Start, TestContext.Current.CancellationToken);
        return accepted;
    }

    private static IDisposable PushUser(string userId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
        );
}
