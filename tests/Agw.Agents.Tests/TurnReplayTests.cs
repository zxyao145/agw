using System.Text.Json;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Inbound.Facades;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Contracts.History;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class TurnReplayTests
{
    [Fact]
    public async Task ReadExistingTurnAsync_ConcurrentSubscribers_ReceiveOneOrderedTurn()
    {
        // Arrange
        using var owner = TurnPersistenceTestKit.EnterUser();
        await using var kit = await TurnPersistenceTestKit.CreateAsync();
        var accepted = await CreateTurnAsync(kit);
        var turns = kit.ResolveScoped<IConversationTurnStore>();
        await accepted.Broadcast!.WriteAsync(accepted.Start, TestContext.Current.CancellationToken);
        await using var first = AgentExecutionFacade
            .ReadExistingTurnAsync(accepted, turns, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var second = AgentExecutionFacade
            .ReadExistingTurnAsync(accepted, turns, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        // Act
        Assert.True(await first.MoveNextAsync());
        Assert.True(await second.MoveNextAsync());
        Assert.Equal(first.Current, second.Current);
        var result = new AgwMessage(
            Guid.CreateVersion7().ToString("D"),
            "agent",
            AiRole.Assistant,
            [new AgwTextContent { Content = "completed output" }]
        );
        await accepted.Broadcast.WriteAsync(result, TestContext.Current.CancellationToken);
        await turns.FinishAsync(
            accepted.Request.TurnId,
            ConversationTurnStatus.Completed,
            1,
            null,
            TestContext.Current.CancellationToken
        );
        await accepted.Broadcast.WriteAsync(
            TurnMessageFactory.CreateFinished(accepted.Request.Envelope, AgwTurnStatus.Completed, 1, null),
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.True(await first.MoveNextAsync());
        Assert.True(await second.MoveNextAsync());
        Assert.Equal(result.MessageId, first.Current.MessageId);
        Assert.Equal(first.Current, second.Current);
        Assert.True(await first.MoveNextAsync());
        Assert.True(await second.MoveNextAsync());
        Assert.True(AgwMessageClassifier.IsTurnFinished(first.Current));
        Assert.Equal(first.Current, second.Current);
        Assert.False(await first.MoveNextAsync());
        Assert.False(await second.MoveNextAsync());
        Assert.Equal(3, accepted.Broadcast.LastSequence);
    }

    [Fact]
    public async Task ReadExistingTurnAsync_BroadcastExpired_ReturnsPersistedOutputAndOutcome()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using var owner = TurnPersistenceTestKit.EnterUser();
        await using var kit = await TurnPersistenceTestKit.CreateAsync();
        var accepted = await CreateTurnAsync(kit);
        var turns = kit.ResolveScoped<IConversationTurnStore>();
        var id = Guid.CreateVersion7();
        await kit.ResolveScoped<IConversationHistoryStore>()
            .UpsertAsync(
                new ConversationMessageWriteScope
                {
                    ProjectId = accepted.Request.Task.ProjectId,
                    ContextId = accepted.Request.Task.ContextId,
                    Generation = 0,
                    ProducerId = accepted.Request.TurnId,
                    TurnId = accepted.Request.TurnId,
                    IsExecutionBound = true,
                },
                [
                    new ConversationMessageSnapshot
                    {
                        MessageId = id,
                        CreatedAt = TimeProvider.System.GetUtcNow(),
                        Metadata = [],
                        Payload = JsonSerializer.Serialize(
                            new ChatMessage(ChatRole.Assistant, "persisted output") { MessageId = id.ToString("D") },
                            WebJsonOptions.Default
                        ),
                    },
                ],
                token
            );
        await turns.FinishAsync(accepted.Request.TurnId, ConversationTurnStatus.Completed, 3, null, token);
        accepted = accepted with { Broadcast = null, Turn = (await turns.GetAsync(accepted.Request.TurnId, token))! };

        // Act
        var messages = await AgentExecutionFacade.ReadExistingTurnAsync(accepted, turns, token).ToListAsync(token);

        // Assert
        Assert.Equal(2, messages.Count);
        Assert.Equal(id.ToString("D"), messages[0].MessageId);
        Assert.Equal("completed", messages[1].AdditionalProperties!["status"]);
        Assert.Equal(3, messages[1].AdditionalProperties!["stepCount"]);
        Assert.DoesNotContain(messages, AgwMessageClassifier.IsTurnStart);
    }

    [Fact]
    public async Task ReadExistingTurnAsync_ActiveTurnUnavailable_RejectsWithoutChangingState()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using var owner = TurnPersistenceTestKit.EnterUser();
        await using var kit = await TurnPersistenceTestKit.CreateAsync();
        var accepted = (await CreateTurnAsync(kit)) with { Broadcast = null };
        var turns = kit.ResolveScoped<IConversationTurnStore>();
        // Act
        await Assert.ThrowsAsync<AgwException>(async () =>
            await AgentExecutionFacade.ReadExistingTurnAsync(accepted, turns, token).ToListAsync(token)
        );
        // Assert
        Assert.Equal(ConversationTurnStatus.Accepted, (await turns.GetAsync(accepted.Request.TurnId, token))!.Status);
    }

    [Fact]
    public async Task ReadExistingTurnAsync_SubscriberCancels_OtherSubscriberReceivesOutcome()
    {
        // 准备两个订阅者，只取消其中一个。
        // Arrange two subscribers and cancel only one of them.
        var token = TestContext.Current.CancellationToken;
        using var owner = TurnPersistenceTestKit.EnterUser();
        await using var kit = await TurnPersistenceTestKit.CreateAsync();
        var accepted = await CreateTurnAsync(kit);
        var turns = kit.ResolveScoped<IConversationTurnStore>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        await accepted.Broadcast!.WriteAsync(accepted.Start, token);
        await using var cancelled = AgentExecutionFacade
            .ReadExistingTurnAsync(accepted, turns, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        await using var continuing = AgentExecutionFacade
            .ReadExistingTurnAsync(accepted, turns, token)
            .GetAsyncEnumerator(token);
        Assert.True(await cancelled.MoveNextAsync());
        Assert.True(await continuing.MoveNextAsync());

        // 取消订阅后，执行仍然可以完成并通知其他订阅者。
        // Act and assert that execution can still complete and notify the other subscriber.
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled.MoveNextAsync());
        await turns.FinishAsync(accepted.Request.TurnId, ConversationTurnStatus.Completed, 1, null, token);
        await accepted.Broadcast.WriteAsync(
            TurnMessageFactory.CreateFinished(accepted.Request.Envelope, AgwTurnStatus.Completed, 1, null),
            token
        );
        Assert.True(await continuing.MoveNextAsync());
        Assert.True(AgwMessageClassifier.IsTurnFinished(continuing.Current));
        Assert.False(await continuing.MoveNextAsync());
        Assert.Equal(ConversationTurnStatus.Completed, (await turns.GetAsync(accepted.Request.TurnId, token))!.Status);
    }

    private static async Task<AcceptedTurn> CreateTurnAsync(TurnPersistenceTestKit kit)
    {
        var task = await kit.SeedConversationAsync();
        var turnId = Guid.CreateVersion7();
        var target = new ExecutionTarget(Guid.CreateVersion7(), AgentRuntimeType.Agent);
        var request = await kit.RegisterTurnAsync(
            new ExecutionRequest(
                turnId,
                TurnPersistenceTestKit.UserId,
                target,
                task,
                TurnPersistenceTestKit.CreateSettings(task.ProjectId),
                TurnPersistenceTestKit.CreateInput("run"),
                true,
                ProjectWorkspacePaths.CreateSnapshot(task.ProjectId, AppContext.BaseDirectory, [])
            )
            {
                Envelope = TurnPersistenceTestKit.CreateEnvelope(turnId, task, target),
            }
        );
        return new AcceptedTurn(
            request,
            TurnMessageFactory.CreateStarted(request.Envelope),
            false,
            (
                await kit.ResolveScoped<IConversationTurnStore>()
                    .GetAsync(turnId, TestContext.Current.CancellationToken)
            )!,
            kit.Broadcasts.Find(turnId, TurnPersistenceTestKit.UserId)
        );
    }
}
