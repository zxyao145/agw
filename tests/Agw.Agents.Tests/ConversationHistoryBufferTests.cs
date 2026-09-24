using System.Security.Claims;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Application.History;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

/// <summary>
/// Turn 历史缓冲的写入模式、容量、屏障、失败与所属校验；写入经 ConversationHistoryWriter 与 AgwChatHistoryProvider 进入缓冲。
/// Write modes, capacity, barrier, failures and ownership checks of the turn history buffer; writes enter it through ConversationHistoryWriter and AgwChatHistoryProvider.
/// </summary>
public sealed class ConversationHistoryBufferTests : IDisposable
{
    private readonly IDisposable _userScope = HistoryTestFixture.EnterUser();

    public void Dispose() => _userScope.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TurnEnd_ManyAppends_ModelSeesImmutablePendingHistoryAndOneCommit()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var initialTime = fixture.Clock.GetUtcNow();
        var writer = fixture.CreateWriter();
        await using (fixture.BeginTurn())
        {
            for (var index = 0; index < 20; index++)
            {
                var message = new ChatMessage(ChatRole.User, $"message-{index}");
                await writer.AppendAsync(fixture.ProjectId, HistoryTestFixture.ContextId, [message], Token);
                message.Contents.Clear();
            }
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Empty(await fixture.ReadAsync());
            Assert.Equal(
                Enumerable.Range(0, 20).Select(index => $"message-{index}"),
                await fixture.ReadModelTextsAsync()
            );
        }

        var records = await fixture.ReadAsync();
        Assert.Equal(20, records.Count);
        Assert.Single(records.Select(record => record.TaskId).Distinct());
        Assert.Equal(
            Enumerable.Range(0, 20).Select(index => (long?)index),
            records.Select(record => record.ConversationSequence)
        );
        Assert.All(records, record => Assert.Equal(initialTime, record.CreateTime));
        Assert.Equal(1, fixture.CommitCount);
    }

    [Fact]
    public async Task Interval_NoFurtherMessages_FlushesAtDeadline()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.Interval);
        await using var turn = fixture.BeginTurn();
        await fixture
            .CreateWriter()
            .AppendAsync(fixture.ProjectId, HistoryTestFixture.ContextId, [new(ChatRole.User, "pending")], Token);
        // 先等待后台计时器登记，再把虚拟时间推进到截止时间之后。
        // Wait for the background delay to register before advancing virtual time past its deadline.
        await fixture
            .Clock.WaitForTimerAsync(TimeSpan.FromSeconds(5), Token)
            .WaitAsync(TimeSpan.FromSeconds(10), Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty(await fixture.ReadAsync());

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.WaitForCommitAsync();

        Assert.Single(await fixture.ReadAsync());
        Assert.Equal(["pending"], await fixture.ReadModelTextsAsync());
        Assert.Equal(1, fixture.CommitCount);
    }

    [Theory]
    [InlineData(ConversationHistoryWriteMode.Immediate, 16777216)]
    [InlineData(ConversationHistoryWriteMode.TurnEnd, 1)]
    [InlineData(ConversationHistoryWriteMode.Interval, 1)]
    public async Task Append_ImmediateOrCapacityLimit_FlushesBeforeReturning(
        ConversationHistoryWriteMode mode,
        long capacity
    )
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(mode, capacity);
        await using var turn = fixture.BeginTurn();
        await fixture
            .CreateWriter()
            .AppendAsync(fixture.ProjectId, HistoryTestFixture.ContextId, [new(ChatRole.User, "capacity")], Token);

        Assert.Single(await fixture.ReadAsync());
        Assert.Equal(1, fixture.CommitCount);
    }

    [Fact]
    public async Task Append_ModeAndTarget_PersistsDisplayMetadata()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var message = new ChatMessage(ChatRole.User, "中文消息 😀")
        {
            AdditionalProperties = new() { ["mode"] = "plan" },
        };
        message.Contents[0].AdditionalProperties = new() { ["targetType"] = "agent", ["targetId"] = "target-1" };
        await using (fixture.BeginTurn())
            await fixture.CreateWriter().AppendAsync(fixture.ProjectId, HistoryTestFixture.ContextId, [message], Token);

        var record = Assert.Single(await fixture.ReadAsync());
        Assert.Equal("plan", record.Metadata!["agentMode"].GetString());
        Assert.Equal("agent", record.Metadata["targetType"].GetString());
        Assert.Equal("target-1", record.Metadata["targetId"].GetString());
        Assert.Equal("中文消息 😀", record.GetText());
    }

    [Fact]
    public async Task ChildScope_Ending_DoesNotFlushOuterTurn()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var turn = fixture.BeginTurn();
        using (turn.Scope.CreateBackgroundAgentScope(Guid.CreateVersion7(), EngineKind.Maf).Push())
            await fixture
                .CreateWriter()
                .AppendAsync(fixture.ProjectId, HistoryTestFixture.ContextId, [new(ChatRole.User, "node")], Token);

        Assert.Empty(await fixture.ReadAsync());
        await turn.Buffer.FlushAsync(Token);
        Assert.Single(await fixture.ReadAsync());
    }

    [Fact]
    public async Task Flush_CommitAcknowledgementFails_RetryDoesNotDuplicateRecords()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var turn = fixture.BeginTurn();
        await fixture
            .CreateWriter()
            .AppendAsync(fixture.ProjectId, HistoryTestFixture.ContextId, [new(ChatRole.User, "once")], Token);
        fixture.ThrowAfterCommit = true;

        await Assert.ThrowsAsync<DbUpdateException>(() => turn.Buffer.FlushAsync(Token));
        var firstId = Assert.Single(await fixture.ReadAsync()).Id;
        Assert.Equal(["once"], await fixture.ReadModelTextsAsync());
        await turn.Buffer.FlushAsync(Token);

        Assert.Equal(firstId, Assert.Single(await fixture.ReadAsync()).Id);
        Assert.Equal(1, fixture.CommitCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Flush_ResetOrDeletion_DoesNotResurrectHistory(bool delete)
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var turn = fixture.BeginTurn();
        await fixture
            .CreateWriter()
            .AppendAsync(fixture.ProjectId, HistoryTestFixture.ContextId, [new(ChatRole.User, "stale")], Token);
        await using (var db = fixture.CreateContext())
        {
            if (delete)
                await db.ProjectConversations.ExecuteDeleteAsync(Token);
            else
                await db.ProjectConversations.ExecuteUpdateAsync(
                    update => update.SetProperty(item => item.Generation, 1),
                    Token
                );
        }
        var error = await Assert.ThrowsAsync<AgwException>(() => turn.Buffer.FlushAsync(Token));
        turn.Scope.RecordFailure(error);

        Assert.Empty(await fixture.ReadAsync());
        await using var check = fixture.CreateContext();
        Assert.Equal(delete ? 0 : 1, await check.ProjectConversations.CountAsync(Token));
    }

    [Fact]
    public async Task Turn_OwnershipLost_DropsPendingHistory()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        using var lost = new CancellationTokenSource();
        var turn = fixture.BeginTurn(ownershipLost: lost);
        await fixture
            .CreateWriter()
            .AppendAsync(fixture.ProjectId, HistoryTestFixture.ContextId, [new(ChatRole.User, "stale")], Token);
        lost.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.DisposeAsync().AsTask());

        Assert.Empty(await fixture.ReadAsync());
    }

    [Fact]
    public async Task TurnHistory_ExecutionFails_PreservesFailureAndFlushesHistory()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var failure = new OperationCanceledException("execution canceled");
        var (scope, pushed) = fixture.PushUnboundScope();
        using (pushed)
        {
            async IAsyncEnumerable<int> Execute()
            {
                await fixture
                    .CreateWriter()
                    .AppendAsync(
                        fixture.ProjectId,
                        HistoryTestFixture.ContextId,
                        [new(ChatRole.User, "before cancel")],
                        Token
                    );
                fixture.ThrowAfterCommit = true;
                yield return 1;
                throw failure;
            }

            var observed = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await foreach (
                    var _ in TurnHistory.RunAsync(scope, fixture.Store, fixture.Conversation(), Execute(), Token)
                ) { }
            });

            Assert.Same(failure, observed);
            Assert.Same(failure, scope.Failure);
        }
        Assert.Single(await fixture.ReadAsync());
    }

    [Fact]
    public async Task Turn_ParallelAppendsAndFlushes_PreservesEveryMessageAndUniqueSequence()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var turn = fixture.BeginTurn();
        var writer = fixture.CreateWriter();
        await Task.WhenAll(
            Enumerable
                .Range(0, 40)
                .Select(async index =>
                {
                    await writer.AppendAsync(
                        fixture.ProjectId,
                        HistoryTestFixture.ContextId,
                        [new(ChatRole.User, index.ToString())],
                        Token
                    );
                    if (index % 7 == 0)
                        await turn.Buffer.FlushAsync(Token);
                    await fixture.ReadModelTextsAsync();
                })
        );
        await turn.Buffer.FlushAsync(Token);

        var rows = await fixture.ReadAsync();
        Assert.Equal(40, rows.Count);
        Assert.Equal(40, rows.Select(row => row.ConversationSequence).Distinct().Count());
        Assert.Equal(40, (await fixture.ReadModelTextsAsync()).Distinct().Count());
    }

    [Fact]
    public async Task Turn_ForeignUser_CannotReadOrAppendPendingMessages()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var turn = fixture.BeginTurn();
        var writer = fixture.CreateWriter();
        await writer.AppendAsync(
            fixture.ProjectId,
            HistoryTestFixture.ContextId,
            [new(ChatRole.User, "private")],
            Token
        );
        using (
            UserInfoUtil.Push(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "other")], "Test"))
            )
        )
        {
            await Assert.ThrowsAsync<AgwException>(() => fixture.ReadModelTextsAsync());
            await Assert.ThrowsAsync<AgwException>(() =>
                writer.AppendAsync(
                    fixture.ProjectId,
                    HistoryTestFixture.ContextId,
                    [new(ChatRole.User, "foreign")],
                    Token
                )
            );
        }
        Assert.Equal(["private"], await fixture.ReadModelTextsAsync());
    }

    [Fact]
    public async Task Barrier_CheckpointBoundary_BlocksConcurrentAppendUntilMarkerIsCommitted()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var turn = fixture.BeginTurn();
        var writer = fixture.CreateWriter();
        await writer.AppendAsync(
            fixture.ProjectId,
            HistoryTestFixture.ContextId,
            [new(ChatRole.User, "before")],
            Token
        );
        Task append;
        await using (await turn.Buffer.EnterBarrierAsync(Token))
        {
            append = writer.AppendAsync(
                fixture.ProjectId,
                HistoryTestFixture.ContextId,
                [new(ChatRole.User, "after")],
                Token
            );
            Assert.False(append.IsCompleted);
            var first = Assert.Single(await fixture.ReadAsync());
            await using var db = fixture.CreateContext();
            db.ProjectConversationChatHistories.Add(
                new ProjectConversationChatHistory
                {
                    Id = Guid.NewGuid(),
                    ConversationId = first.ConversationId,
                    TaskId = Guid.NewGuid(),
                    Status = TaskExecutionStatus.Succeeded,
                    ConversationSequence = 1,
                    ConversationPayload = System.Text.Json.JsonSerializer.Serialize(
                        new ChatMessage(ChatRole.User, "checkpoint"),
                        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                    ),
                    CreateTime = fixture.Clock.GetUtcNow(),
                    UpdateTime = fixture.Clock.GetUtcNow(),
                }
            );
            await db.SaveConversationChangesAsync(first.ConversationId, 0, Token);
        }
        await append;
        await turn.Buffer.FlushAsync(Token);

        Assert.Equal(["before", "checkpoint", "after"], await fixture.ReadModelTextsAsync());
    }

    [Fact]
    public async Task Turn_ResumedGeneration_FlowsFromExecutionIdentityAndRestoresCallerContext()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await fixture.SetGenerationAsync(1);
        await using (fixture.BeginTurn(generation: 1))
        {
            await fixture
                .CreateWriter()
                .AppendAsync(
                    fixture.ProjectId,
                    HistoryTestFixture.ContextId,
                    [new(ChatRole.User, "new generation")],
                    Token
                );
            Assert.Equal(["new generation"], await fixture.ReadModelTextsAsync());
        }

        Assert.Single(await fixture.ReadAsync());
        Assert.Null(ExecutionContextSlot.FindBound(fixture.ProjectId, HistoryTestFixture.ContextId));
    }

    [Fact]
    public async Task PendingHistory_NodeScopes_RemainIsolatedBeforeAndAfterFlush()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        await using var turn = fixture.BeginTurn();
        var history = fixture.CreateProvider(EngineKind.Pi);
        var agent = new HistoryNotifyingAgent(history);
        foreach (var node in new[] { "node-a", "node-b" })
        {
            var session = await agent.CreateSessionAsync(Token);
            fixture.SessionState.InitializeSessionState(session, HistoryTestFixture.ContextId, fixture.ProjectId, node);
            await HistoryTestFixture.RunSdkCallbacksAsync(history, agent, session, [new(ChatRole.User, node)]);
        }
        Assert.Equal(["node-a"], await fixture.ReadModelTextsAsync("node-a"));
        Assert.Equal(["node-b"], await fixture.ReadModelTextsAsync("node-b"));
        await turn.Buffer.FlushAsync(Token);
        Assert.Equal(["node-a"], await fixture.ReadModelTextsAsync("node-a"));
    }

    [Fact]
    public async Task Interval_TransientFailure_RetainsBatchAndRetriesNextInterval()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.Interval);
        await using var turn = fixture.BeginTurn();
        await fixture
            .CreateWriter()
            .AppendAsync(fixture.ProjectId, HistoryTestFixture.ContextId, [new(ChatRole.User, "retry")], Token);
        fixture.FailNextContext = true;
        await fixture
            .Clock.WaitForTimerAsync(TimeSpan.FromSeconds(5), Token)
            .WaitAsync(TimeSpan.FromSeconds(10), Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.ContextFailure.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(["retry"], await fixture.ReadModelTextsAsync());
        Assert.Empty(await fixture.ReadAsync());
        await fixture
            .Clock.WaitForTimerAsync(TimeSpan.FromSeconds(5), Token)
            .WaitAsync(TimeSpan.FromSeconds(10), Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.WaitForCommitAsync();

        Assert.Single(await fixture.ReadAsync());
    }

    [Fact]
    public async Task TurnHistory_MultipleMoveNextCalls_RetainsPendingHistoryUntilCompletion()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var writer = fixture.CreateWriter();
        var (scope, pushed) = fixture.PushUnboundScope();
        using (pushed)
        {
            async IAsyncEnumerable<int> Execute()
            {
                await writer.AppendAsync(
                    fixture.ProjectId,
                    HistoryTestFixture.ContextId,
                    [new(ChatRole.User, "first")],
                    Token
                );
                yield return 1;
                await writer.AppendAsync(
                    fixture.ProjectId,
                    HistoryTestFixture.ContextId,
                    [new(ChatRole.User, "second")],
                    Token
                );
                yield return 2;
            }

            await foreach (
                var _ in TurnHistory.RunAsync(scope, fixture.Store, fixture.Conversation(), Execute(), Token)
            )
                Assert.Empty(await fixture.ReadAsync());
        }
        Assert.Equal(2, (await fixture.ReadAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurnHistory_CancelOrEarlyDisposal_FlushesPendingHistory(bool cancel)
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var failure = new OperationCanceledException("stream canceled");
        var (scope, pushed) = fixture.PushUnboundScope();
        using (pushed)
        {
            async IAsyncEnumerable<int> Execute()
            {
                await fixture
                    .CreateWriter()
                    .AppendAsync(
                        fixture.ProjectId,
                        HistoryTestFixture.ContextId,
                        [new(ChatRole.User, "before yield")],
                        Token
                    );
                yield return 1;
                throw failure;
            }

            var source = TurnHistory.RunAsync(scope, fixture.Store, fixture.Conversation(), Execute(), Token);
            await using (var enumerator = source.GetAsyncEnumerator(Token))
            {
                Assert.True(await enumerator.MoveNextAsync());
                if (cancel)
                    Assert.Same(
                        failure,
                        await Assert.ThrowsAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask())
                    );
            }
        }
        Assert.Single(await fixture.ReadAsync());
    }

    [Fact]
    public async Task Turn_FinalFlushConflictWithoutExecutionFailure_Throws()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var turn = fixture.BeginTurn();
        await fixture
            .CreateWriter()
            .AppendAsync(fixture.ProjectId, HistoryTestFixture.ContextId, [new(ChatRole.User, "waiting")], Token);
        await fixture.SetGenerationAsync(1);

        var error = await Assert.ThrowsAsync<AgwException>(() => turn.DisposeAsync().AsTask());
        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, error.Code);
        Assert.Empty(await fixture.ReadAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NodeInput_ModelCall_PreservesIdentitySourceAndNodeIsolation(bool complete)
    {
        // Arrange
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var input = new ChatMessage(ChatRole.User, "upstream review")
        {
            MessageId = Guid.CreateVersion7().ToString("N"),
            AuthorName = "pi",
            AdditionalProperties = new()
            {
                [ConversationHistoryMetadata.AgentflowInputKey] = true,
                ["nodeName"] = "Review",
            },
        };
        var history = fixture.CreateProvider();
        var agent = new HistoryNotifyingAgent(history);

        // Act: 完成的调用与中断的调用都保留已写入的输入。
        // Act: both a completed call and an interrupted call retain the written input.
        await using (fixture.BeginTurn())
        {
            var session = await agent.CreateSessionAsync(Token);
            fixture.SessionState.InitializeSessionState(
                session,
                HistoryTestFixture.ContextId,
                fixture.ProjectId,
                "node:b",
                "Implement"
            );
            await history.InvokingAsync(new ChatHistoryProvider.InvokingContext(agent, session, [input]), Token);
            if (complete)
                await history.InvokedAsync(
                    new ChatHistoryProvider.InvokedContext(agent, session, [input], [new(ChatRole.Assistant, "done")]),
                    Token
                );
        }

        // Assert
        var rows = await fixture.ReadAsync();
        var restored = rows[0].ToChatMessage()!;
        Assert.Equal(Guid.Parse(input.MessageId), Guid.Parse(restored.MessageId!));
        Assert.Equal(input.Text, restored.Text);
        Assert.Equal(input.AuthorName, restored.AuthorName);
        Assert.Equal("Review", restored.AdditionalProperties!["nodeName"]?.ToString());
        Assert.Equal(
            bool.TrueString,
            restored.AdditionalProperties[ConversationHistoryMetadata.AgentflowInputKey]?.ToString(),
            ignoreCase: true
        );
        Assert.Equal(complete ? 2 : 1, rows.Count);
        Assert.Contains("upstream review", await fixture.ReadModelTextsAsync("node:b"));
        Assert.Empty(await fixture.ReadModelTextsAsync("node:c"));
    }

    [Fact]
    public async Task StreamedResponse_UncertainCommitThenCompletion_UpdatesStableRecordAndPendingModelView()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var history = fixture.CreateProvider();
        var agent = new HistoryNotifyingAgent(history);
        await using (var turn = fixture.BeginTurn())
        {
            var session = await agent.CreateSessionAsync(Token);
            fixture.SessionState.InitializeSessionState(
                session,
                HistoryTestFixture.ContextId,
                fixture.ProjectId,
                "node:1",
                "First node"
            );
            var recording = history.Begin(agent, session);
            recording.Stage([new ChatMessage(ChatRole.User, "question")]);
            await history.InvokingAsync(new ChatHistoryProvider.InvokingContext(agent, session, []), Token);
            var update = new AgentResponseUpdate(ChatRole.Assistant, "first")
            {
                MessageId = "answer",
                AdditionalProperties = new() { ["modelName"] = "test-model" },
            };
            await recording.RecordAsync(update, Token);
            update.Contents.Clear();
            fixture.ThrowAfterCommit = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => turn.Buffer.FlushAsync(Token));
            var first = await fixture.ReadAsync();
            Assert.Equal(2, first.Count);
            Assert.Equal("first", first[1].GetText());
            Assert.Equal(["question"], await fixture.ReadModelTextsAsync("node:1"));
            await recording.RecordAsync(
                new AgentResponseUpdate(ChatRole.Assistant, " second") { MessageId = "answer" },
                Token
            );
            await turn.Buffer.FlushAsync(Token);
            var second = await fixture.ReadAsync();
            Assert.Equal(first[1].Id, second[1].Id);
            Assert.Equal("first second", second[1].GetText());
            await history.InvokedAsync(
                new ChatHistoryProvider.InvokedContext(
                    agent,
                    session,
                    [],
                    [
                        new ChatMessage(ChatRole.Assistant, "first second")
                        {
                            MessageId = "answer",
                            AdditionalProperties = new() { ["modelName"] = "test-model" },
                        },
                    ]
                ),
                Token
            );
            await recording.FinishAsync(completed: true, Token);
            history.End(session);
            Assert.Equal(["question", "first second"], await fixture.ReadModelTextsAsync("node:1"));
            Assert.Empty(await fixture.ReadModelTextsAsync("node:2"));
        }
        var completed = await fixture.ReadAsync();
        Assert.Equal(2, completed.Count);
        Assert.Equal(completed[0].TaskId, completed[1].TaskId);
        var message = completed[1].ToChatMessage()!;
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(message));
        Assert.Equal("test-model", message.AdditionalProperties!["modelName"]?.ToString());
        Assert.Equal("First node", message.AdditionalProperties!["nodeName"]?.ToString());
    }

    [Theory]
    [InlineData(ConversationHistoryWriteMode.Immediate, 16777216)]
    [InlineData(ConversationHistoryWriteMode.TurnEnd, 1)]
    public async Task StreamedResponse_ImmediateOrCapacityTrigger_CommitsAndReplacesSnapshot(
        ConversationHistoryWriteMode mode,
        long capacity
    )
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(mode, capacity);
        var history = fixture.CreateProvider();
        var agent = new HistoryNotifyingAgent(history);
        await using (fixture.BeginTurn())
        {
            var session = await fixture.CreateSessionAsync(agent);
            var recording = history.Begin(agent, session);
            await recording.RecordAsync(new AgentResponseUpdate(ChatRole.Assistant, "partial"), Token);
            var first = Assert.Single(await fixture.ReadAsync());
            await recording.CalibrateAsync([new ChatMessage(ChatRole.Assistant, "complete")], null, Token);
            var final = Assert.Single(await fixture.ReadAsync());
            Assert.Equal(first.Id, final.Id);
            Assert.Equal("complete", final.GetText());
            history.End(session);
        }
    }

    [Fact]
    public async Task StreamedResponse_ResetAfterSnapshot_RejectsLateFinalUpdate()
    {
        await using var fixture = await HistoryTestFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var history = fixture.CreateProvider();
        var agent = new HistoryNotifyingAgent(history);
        var turn = fixture.BeginTurn();
        var session = await fixture.CreateSessionAsync(agent);
        var recording = history.Begin(agent, session);
        await recording.RecordAsync(new AgentResponseUpdate(ChatRole.Assistant, "partial"), Token);
        await turn.Buffer.FlushAsync(Token);
        await fixture.SetGenerationAsync(1);
        await recording.CalibrateAsync([new ChatMessage(ChatRole.Assistant, "late complete")], null, Token);
        history.End(session);

        var failure = await Assert.ThrowsAsync<AgwException>(() => turn.DisposeAsync().AsTask());
        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, failure.Code);
        Assert.Equal("partial", Assert.Single(await fixture.ReadAsync()).GetText());
    }
}
