using System.Globalization;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Application.History;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

/// <summary>
/// 受理事务：输入行、Turn 行与 Durable 记录在一个事务中提交，turnId 幂等且只对所属用户与对话可见。
/// The acceptance transaction: the input row, turn row and durable record commit in one transaction, and turnIds are idempotent and visible only to their owner and conversation.
/// </summary>
public sealed class TurnAcceptanceTests : IAsyncLifetime
{
    private const string UserId = TurnPersistenceTestKit.UserId;

    private readonly IDisposable _user = TurnPersistenceTestKit.EnterUser();
    private TurnPersistenceTestKit _kit = null!;

    public async ValueTask InitializeAsync() => _kit = await TurnPersistenceTestKit.CreateAsync();

    public async ValueTask DisposeAsync()
    {
        _user.Dispose();
        await _kit.DisposeAsync();
    }

    [Fact]
    public async Task AcceptAsync_InProcess_WritesInputRowAndTurnBeforeStart()
    {
        // Arrange
        var task = await _kit.SeedConversationAsync();
        var input = TurnPersistenceTestKit.CreateInput("hello");

        // Act
        var accepted = await AcceptAsync(task, input: input);

        // Assert
        Assert.True(accepted.Created);
        Assert.NotNull(accepted.Broadcast);
        Assert.True(AgwMessageClassifier.IsTurnStart(accepted.Start));
        Assert.Equal(1L, accepted.Start.AdditionalProperties?[TurnMessageFactory.TurnSequenceKey]);
        Assert.Equal(input.MessageId, accepted.Start.AdditionalProperties?["streamingScopeId"]);
        var turnId = accepted.Request.TurnId;
        var inputId = Guid.Parse(input.MessageId);
        var turn = await _kit.ReadTurnAsync(turnId);
        Assert.Equal(ProjectConversationTurnStatus.Accepted, turn.Status);
        Assert.Equal(task.ProjectConversationId, turn.ProjectConversationId);
        Assert.Equal(inputId, turn.InputMessageId);
        Assert.Equal(0, turn.FirstSequence);
        var row = Assert.Single(await _kit.ReadHistoryAsync(task.ProjectConversationId));
        Assert.Equal(inputId, row.Id);
        Assert.Equal(turnId, row.TurnId);
        Assert.Equal(0, row.StepIndex);
        Assert.Equal(ConversationMessagePurpose.Input, row.Purpose);
        Assert.Null(row.HistoryScope);
        Assert.Equal(0, row.Metadata![ConversationHandoffMetadata.ThroughSequenceKey].GetInt64());
        Assert.Equal(turnId, row.Metadata["producerScopeId"].GetGuid());
        Assert.Equal("hello", row.GetText());
        await using var context = _kit.CreateContext();
        Assert.False(
            await context.DurableExecutions.IgnoreQueryFilters().AnyAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task AcceptAsync_Durable_CommitsRecordWithStartEvent()
    {
        // Arrange
        var task = await _kit.SeedConversationAsync();

        // Act
        var accepted = await AcceptAsync(task, durable: _kit.Coordinator);

        // Assert
        var turnId = accepted.Request.TurnId;
        Assert.Null(accepted.Request.Lease);
        var record = await _kit.ReadExecutionAsync(turnId);
        Assert.Equal(DurableExecutionStatus.Queued, record.Status);
        Assert.Equal(0, record.LeaseEpoch);
        Assert.Null(record.WorkerId);
        Assert.Equal(1, record.LastEventSequence);
        var start = DurableExecutionEvents.ToEntry(Assert.Single(await _kit.ReadEventsAsync(turnId)));
        Assert.Equal(1, start.Sequence);
        Assert.True(AgwMessageClassifier.IsTurnStart(start.Message));
        Assert.Equal(
            turnId.ToString("D"),
            start.Message.AdditionalProperties?[TurnMessageFactory.TurnIdKey]?.ToString()
        );
        Assert.Equal(ProjectConversationTurnStatus.Accepted, (await _kit.ReadTurnAsync(turnId)).Status);
    }

    [Fact]
    public async Task AcceptAsync_TurnIdOfAnotherUserOrConversation_FailsLikeMissingConversation()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var owned = await _kit.SeedConversationAsync();
        var otherConversation = await _kit.SeedConversationAsync(contextId: "context-2");
        var turnId = (await AcceptAsync(owned)).Request.TurnId;
        AgentExecutionTask foreignTask;
        using (TurnPersistenceTestKit.EnterUser("another-user"))
            foreignTask = await _kit.SeedConversationAsync("another-user");

        // Act
        var otherConversationError = await Assert.ThrowsAsync<AgwException>(() =>
            AcceptAsync(otherConversation, turnId)
        );
        AgwException foreignUserError;
        using (TurnPersistenceTestKit.EnterUser("another-user"))
            foreignUserError = await Assert.ThrowsAsync<AgwException>(() =>
                AcceptAsync(foreignTask, turnId, userId: "another-user")
            );

        // Assert
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, otherConversationError.Code);
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, foreignUserError.Code);
        Assert.Empty(await _kit.ReadHistoryAsync(otherConversation.ProjectConversationId));
        Assert.Empty(await _kit.ReadHistoryAsync(foreignTask.ProjectConversationId));
        Assert.Equal(owned.ProjectConversationId, (await _kit.ReadTurnAsync(turnId)).ProjectConversationId);
        await using var context = _kit.CreateContext();
        Assert.Equal(1, await context.ProjectConversationTurns.IgnoreQueryFilters().CountAsync(token));
    }

    [Fact]
    public async Task AcceptAsync_DurableRegistrationFails_RollsBackInputAndTurn()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();
        var turnId = Guid.CreateVersion7();
        await using (var context = _kit.CreateContext())
        {
            // 同一 ID 的执行记录已经存在且没有 Turn 行：登记时主键冲突，整个受理事务回滚。
            // An execution record with the same ID exists without a turn row: registration hits a key conflict and the whole acceptance rolls back.
            context.DurableExecutions.Add(
                new DurableExecutionRecord
                {
                    Id = turnId,
                    UserId = "another-user",
                    ManifestJson = "{}",
                    StateChangedAt = DateTimeOffset.UtcNow,
                    StateVersion = Guid.CreateVersion7(),
                }
            );
            await context.SaveChangesAsync(token);
        }

        // Act
        var error = await Assert.ThrowsAsync<AgwException>(() => AcceptAsync(task, turnId, durable: _kit.Coordinator));

        // Assert
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, error.Code);
        Assert.Empty(await _kit.ReadHistoryAsync(task.ProjectConversationId));
        await using var assertContext = _kit.CreateContext();
        Assert.False(await assertContext.ProjectConversationTurns.IgnoreQueryFilters().AnyAsync(token));
        Assert.False(
            await assertContext
                .DurableExecutionEvents.IgnoreQueryFilters()
                .AnyAsync(entry => entry.TurnId == turnId, token)
        );
    }

    [Fact]
    public async Task ReportStartFailureAsync_InProcess_WritesErrorAndFailedFinishAfterStart()
    {
        // Arrange
        var task = await _kit.SeedConversationAsync();
        var acceptance = CreateAcceptance();
        var accepted = await AcceptAsync(task, acceptance: acceptance);
        var sink = new InteractionTestSink();
        accepted.Broadcast!.AddSink(sink);
        await accepted.Broadcast.WriteAsync(accepted.Start, TestContext.Current.CancellationToken);

        // Act
        await acceptance.ReportStartFailureAsync(
            accepted,
            new AgwException(ErrorCodes.AgentExecutionFailed, "start failed")
        );

        // Assert
        Assert.Equal(
            [1L, 2L, 3L],
            sink.Messages.Select(message => message.AdditionalProperties?[TurnMessageFactory.TurnSequenceKey])
        );
        Assert.True(AgwMessageClassifier.IsTurnStart(sink.Messages[0]));
        Assert.Equal("start failed", Assert.IsType<AgwErrorContent>(Assert.Single(sink.Messages[1].Contents)).Content);
        Assert.True(AgwMessageClassifier.TryGetTurnFinishedStatus(sink.Messages[2], out var status));
        Assert.Equal(AgwTurnStatus.Failed, status);
        var errorCode = ErrorCodes.AgentExecutionFailed.Code.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(errorCode, sink.Messages[2].AdditionalProperties?["errorCode"]);
        var turn = await _kit.ReadTurnAsync(accepted.Request.TurnId);
        Assert.Equal(ProjectConversationTurnStatus.Failed, turn.Status);
        Assert.Equal(errorCode, turn.ErrorCode);
        Assert.Equal(0, turn.LastSequence);
    }

    private TurnAcceptanceService CreateAcceptance(DurableExecutionCoordinator? durable = null) =>
        _kit.CreateAcceptance(projectTasks: null, new AgentExecutionFacadeTests.WorkspaceProjects(), durable);

    private Task<AcceptedTurn> AcceptAsync(
        AgentExecutionTask task,
        Guid? turnId = null,
        DurableExecutionCoordinator? durable = null,
        AgwUserInput? input = null,
        string userId = UserId,
        TurnAcceptanceService? acceptance = null
    ) =>
        (acceptance ?? CreateAcceptance(durable)).AcceptAsync(
            new TurnAcceptanceRequest(
                userId,
                turnId,
                new ExecutionTarget(Guid.CreateVersion7(), AgentRuntimeType.Agent),
                task.ProjectConversationId,
                input ?? TurnPersistenceTestKit.CreateInput("hello"),
                TurnPersistenceTestKit.CreateSettings(task.ProjectId, task.ProjectConversationId),
                Stream: true
            )
            {
                Task = task,
            },
            TestContext.Current.CancellationToken
        );
}
