using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed partial class DurableExecutionStoreTests
{
    [Theory]
    [InlineData(AgentRuntimeType.Agent)]
    [InlineData(AgentRuntimeType.Agentflow)]
    public async Task StartAsync_DurableWithoutLocalWorker_QueuesAndReplaysStartOnEveryAttach(
        AgentRuntimeType agentType
    )
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();
        var turnId = Guid.CreateVersion7();
        var starts = new List<(ExecutionReceipt Receipt, Guid? ActiveId, AgwMessage Start)>();

        // Act: resend the same turn on another connection, with no worker running.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var accepted = await AcceptAsync(task, turnId, agentType: agentType);
            IExecutionCoordinator starter = _kit.Coordinator;
            var receipt = await starter.StartAsync(accepted.Request, token).WaitAsync(TimeSpan.FromSeconds(5), token);
            var sink = new StarterMessageSink();
            await using var attachment = new DurableExecutionAttachment(UserId, sink, token, _kit.Coordinator);
            await attachment.AttachAsync(receipt.TurnId, cursor: null, task.ProjectConversationId, token);
            starts.Add(
                (receipt, attachment.ActiveExecutionId, await sink.First.WaitAsync(TimeSpan.FromSeconds(5), token))
            );
        }

        // Assert
        Assert.All(
            starts,
            entry =>
            {
                Assert.True(entry.Receipt.Accepted);
                Assert.Equal(turnId, entry.Receipt.TurnId);
                Assert.Equal(turnId, entry.ActiveId);
                Assert.True(AgwMessageClassifier.IsTurnStart(entry.Start));
                Assert.Equal(1L, entry.Start.AdditionalProperties?["turnSequence"]);
            }
        );
        var snapshot = await Store.GetAsync(turnId, token);
        Assert.Equal(DurableExecutionStatus.Queued, snapshot.Status);
        Assert.Equal(agentType, snapshot.Manifest.AgentType);
        Assert.Equal(_agentId, snapshot.Manifest.AgentId);
        Assert.Equal(task.ProjectConversationId, snapshot.Manifest.Task.ProjectConversationId);
        Assert.Equal(UserId, snapshot.Manifest.UserId);
        await using var context = _kit.CreateContext();
        Assert.Single(await context.DurableExecutions.IgnoreQueryFilters().ToListAsync(token));
    }

    [Fact]
    public async Task AcceptAsync_DurableNonStreaming_RejectsWithoutWritingTurn()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() => AcceptAsync(task, stream: false));

        // Assert
        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        await using var context = _kit.CreateContext();
        Assert.Empty(await context.DurableExecutions.IgnoreQueryFilters().ToListAsync(token));
        Assert.Empty(await context.ProjectConversationTurns.IgnoreQueryFilters().ToListAsync(token));
        Assert.Empty(await _kit.ReadHistoryAsync(task.ProjectConversationId));
    }

    private sealed class StarterMessageSink : IExecutionMessageSink
    {
        private readonly TaskCompletionSource<AgwMessage> _first = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task<AgwMessage> First => _first.Task;

        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
        {
            _first.TrySetResult(message);
            return ValueTask.CompletedTask;
        }
    }
}
