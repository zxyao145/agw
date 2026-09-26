using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Agentflows.Checkpoints.Durable;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Infrastructure.Agents;
using Agw.Projects.Contracts.History;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public sealed partial class DurableExecutionStoreTests
{
    [Fact]
    public async Task AppendAsync_ConcurrentCommits_AssignContiguousSequences()
    {
        // Arrange
        var id = await RegisterAsync();
        var lease = await ClaimAsync(id);
        using var ownership = new CancellationTokenSource();
        var guard = _kit.Leases.CreateGuard(lease, ownership);

        // Act
        var committed = await Task.WhenAll(
            Enumerable
                .Range(0, 16)
                .Select(index => Task.Run(() => AppendAsync(guard, id, lease.Epoch, TextEvent($"event-{index}"))))
        );

        // Assert
        var sequences = committed.Select(entries => Assert.Single(entries).Sequence).Order().ToArray();
        Assert.Equal(Enumerable.Range(2, 16).Select(value => (long)value), sequences);
        Assert.All(
            committed,
            entries => Assert.Equal(entries[0].Sequence, entries[0].Message.AdditionalProperties?["turnSequence"])
        );
        Assert.Equal(
            Enumerable.Range(1, 17).Select(value => (long)value),
            (await _kit.ReadEventsAsync(id)).Select(entry => entry.TurnSequence)
        );
        Assert.Equal(17, (await _kit.ReadExecutionAsync(id)).LastEventSequence);
    }

    [Fact]
    public async Task AppendAsync_RetriedBatch_KeepsIdsSequencesAndCounter()
    {
        // Arrange
        var id = await RegisterAsync();
        var lease = await ClaimAsync(id);
        using var ownership = new CancellationTokenSource();
        var guard = _kit.Leases.CreateGuard(lease, ownership);
        var batch = new[] { TextEvent("first"), TextEvent("second") };
        var first = await AppendAsync(guard, id, lease.Epoch, batch);

        // Act
        var retried = await AppendAsync(guard, id, lease.Epoch, lookupCommitted: true, batch);
        var conflict = await Assert.ThrowsAsync<AgwException>(() =>
            AppendAsync(
                guard,
                id,
                lease.Epoch,
                lookupCommitted: true,
                new PendingExecutionEvent(batch[0].EventId, TextMessage("changed"))
            )
        );

        // Assert
        Assert.Equal([2L, 3L], first.Select(entry => entry.Sequence));
        Assert.Equal(first.Select(entry => entry.Sequence), retried.Select(entry => entry.Sequence));
        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, conflict.Code);
        Assert.Equal(3, (await _kit.ReadExecutionAsync(id)).LastEventSequence);
        Assert.Equal([id, id, id], (await _kit.ReadEventsAsync(id)).Select(entry => entry.TurnId));
    }

    [Fact]
    public async Task ReadAsync_CommittedButNotBroadcast_ReplaysFromEventLog()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var id = await RegisterAsync();
        var lease = await ClaimAsync(id);
        using var ownership = new CancellationTokenSource();
        await AppendAsync(
            _kit.Leases.CreateGuard(lease, ownership),
            id,
            lease.Epoch,
            TextEvent("first"),
            TextEvent("second")
        );

        // Act
        var replayed = new List<TurnBroadcastEntry>();
        await foreach (var entry in _kit.Coordinator.ReadAsync(id, UserId, afterSequence: 1, token))
        {
            replayed.Add(entry);
            if (replayed.Count == 2)
                break;
        }

        // Assert
        Assert.Equal([2L, 3L], replayed.Select(entry => entry.Sequence));
        Assert.Equal(
            ["first", "second"],
            replayed.Select(entry => Assert.IsType<AgwTextContent>(Assert.Single(entry.Message.Contents)).Content)
        );
        Assert.All(
            replayed,
            entry => Assert.Equal(id.ToString("D"), entry.Message.AdditionalProperties?[TurnMessageFactory.TurnIdKey])
        );
    }

    [Fact]
    public async Task TakenOverLease_EveryExecutionWrite_IsRejected()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();
        var accepted = await AcceptAsync(task);
        var id = accepted.Request.TurnId;
        var oldLease = await ClaimAsync(id, "worker-a");
        _clock.Advance(LeaseDuration);
        var newLease = await ClaimAsync(id, "worker-b");
        var historyBefore = await _kit.ReadHistoryAsync(task.ProjectConversationId);

        // Act
        var writes = new Dictionary<string, Func<IExecutionWriteGuard, CancellationToken, Task>>
        {
            ["events"] = (guard, _) => AppendAsync(guard, id, oldLease.Epoch, TextEvent("late")),
            ["segment result"] = (guard, _) =>
                guard.RunAsync(
                    (services, writeToken) =>
                        services
                            .GetRequiredService<DurableExecutionStore>()
                            .ApplySegmentResultAsync(
                                new DurableExecutionSegmentResult
                                {
                                    ExecutionId = id,
                                    SegmentIndex = 0,
                                    Status = DurableExecutionSegmentStatus.Completed,
                                },
                                writeToken
                            ),
                    token
                ),
            ["turn step"] = (guard, _) =>
                new TurnRecord(
                    _kit.Services.GetRequiredService<IServiceScopeFactory>(),
                    id,
                    accepted.Request.InputMessageId,
                    guard
                ).CompleteStepAsync(1, token),
            ["turn finish"] = (guard, _) =>
                new TurnRecord(
                    _kit.Services.GetRequiredService<IServiceScopeFactory>(),
                    id,
                    accepted.Request.InputMessageId,
                    guard
                ).FinishAsync(ConversationTurnStatus.Completed, 1, null, token),
            ["history"] = (guard, ownershipLost) => FlushLateHistoryAsync(task, id, guard, ownershipLost),
            ["checkpoint"] = (guard, _) => RecordLateCheckpointAsync(task, id, guard),
        };
        var rejected = new Dictionary<string, (AgwException Error, bool OwnershipLost)>();
        foreach (var (name, write) in writes)
        {
            using var ownership = new CancellationTokenSource();
            var guard = _kit.Leases.CreateGuard(oldLease, ownership);
            var error = await Assert.ThrowsAsync<AgwException>(() => write(guard, ownership.Token));
            rejected[name] = (error, ownership.IsCancellationRequested);
        }

        // Assert
        Assert.All(
            rejected,
            entry =>
            {
                Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, entry.Value.Error.Code);
                Assert.True(entry.Value.OwnershipLost, entry.Key);
            }
        );
        var record = await _kit.ReadExecutionAsync(id);
        Assert.Equal(DurableExecutionStatus.Running, record.Status);
        Assert.Equal("worker-b", record.WorkerId);
        Assert.Equal(1, record.LastEventSequence);
        Assert.Single(await _kit.ReadEventsAsync(id));
        var turn = await _kit.ReadTurnAsync(id);
        Assert.Equal(ProjectConversationTurnStatus.Accepted, turn.Status);
        Assert.Equal(0, turn.StepCount);
        Assert.Equal(
            historyBefore.Select(row => row.Id),
            (await _kit.ReadHistoryAsync(task.ProjectConversationId)).Select(row => row.Id)
        );
        await using (var context = _kit.CreateContext())
            Assert.False(await context.AgentflowCheckpoints.IgnoreQueryFilters().AnyAsync(token));
        using var newOwnership = new CancellationTokenSource();
        var accepted2 = await AppendAsync(
            _kit.Leases.CreateGuard(newLease, newOwnership),
            id,
            newLease.Epoch,
            TextEvent("current")
        );
        Assert.Equal(2, Assert.Single(accepted2).Sequence);
        Assert.False(newOwnership.IsCancellationRequested);
    }

    [Fact]
    public async Task FailAcceptedAsync_QueuedTurn_CommitsErrorAndFailedFinish()
    {
        // Arrange
        var accepted = await AcceptAsync();
        var id = accepted.Request.TurnId;
        var acceptance = _kit.CreateAcceptance(
            projectTasks: null,
            new AgentExecutionFacadeTests.WorkspaceProjects(),
            _kit.Coordinator
        );

        // Act
        await acceptance.ReportStartFailureAsync(accepted, new AgwException(ErrorCodes.ConversationSessionConflict));

        // Assert
        var record = await _kit.ReadExecutionAsync(id);
        Assert.Equal(DurableExecutionStatus.Failed, record.Status);
        Assert.Equal(3, record.LastEventSequence);
        var events = (await _kit.ReadEventsAsync(id)).Select(DurableExecutionEvents.ToEntry).ToArray();
        Assert.Equal([1L, 2L, 3L], events.Select(entry => entry.Sequence));
        Assert.True(AgwMessageClassifier.IsTurnStart(events[0].Message));
        Assert.IsType<AgwErrorContent>(Assert.Single(events[1].Message.Contents));
        Assert.True(AgwMessageClassifier.TryGetTurnFinishedStatus(events[2].Message, out var status));
        Assert.Equal(AgwTurnStatus.Failed, status);
        var errorCode = ErrorCodes.ConversationSessionConflict.Code.ToString(
            System.Globalization.CultureInfo.InvariantCulture
        );
        Assert.Equal(errorCode, events[2].Message.AdditionalProperties?["errorCode"]?.ToString());
        var turn = await _kit.ReadTurnAsync(id);
        Assert.Equal(ProjectConversationTurnStatus.Failed, turn.Status);
        Assert.Equal(errorCode, turn.ErrorCode);
        Assert.NotNull(turn.FinishedAt);
    }

    private static Task<IReadOnlyList<TurnBroadcastEntry>> AppendAsync(
        IExecutionWriteGuard guard,
        Guid turnId,
        long leaseEpoch,
        params PendingExecutionEvent[] events
    ) => AppendAsync(guard, turnId, leaseEpoch, lookupCommitted: false, events);

    private static Task<IReadOnlyList<TurnBroadcastEntry>> AppendAsync(
        IExecutionWriteGuard guard,
        Guid turnId,
        long leaseEpoch,
        bool lookupCommitted,
        params PendingExecutionEvent[] events
    ) =>
        guard.RunAsync(
            (services, token) =>
                DurableExecutionEvents.AppendAsync(
                    services.GetRequiredService<IAgentsDbContext>(),
                    services.GetRequiredService<IDurableExecutionEventSequence>(),
                    turnId,
                    leaseEpoch,
                    0,
                    events,
                    lookupCommitted,
                    token
                ),
            TestContext.Current.CancellationToken
        );

    private static PendingExecutionEvent TextEvent(string text) => PendingExecutionEvent.Create(TextMessage(text));

    private static AgwMessage TextMessage(string text) =>
        new(Guid.CreateVersion7().ToString("D"), "agent", AiRole.Assistant, [new AgwTextContent { Content = text }]);

    /// <summary>
    /// 用旧租约的写入入口提交一条执行历史，与 Segment 历史缓冲的提交方式一致。
    /// Commits one execution history row through the old lease's write guard, as the segment history buffer does.
    /// </summary>
    private async Task FlushLateHistoryAsync(
        AgentExecutionTask task,
        Guid turnId,
        IExecutionWriteGuard guard,
        CancellationToken ownershipLost
    )
    {
        var buffer = _kit
            .Services.GetRequiredService<IConversationHistoryStore>()
            .BeginBuffer(
                new ConversationHistoryScope
                {
                    ProjectId = task.ProjectId,
                    ContextId = task.ContextId,
                    Generation = task.Generation,
                    IsExecutionBound = true,
                },
                ownershipLost,
                guard
            );
        AgwException? failure = null;
        try
        {
            var messageId = Guid.CreateVersion7();
            await buffer.ScheduleAsync(
                new ConversationMessageWriteScope
                {
                    ProjectId = task.ProjectId,
                    ContextId = task.ContextId,
                    Generation = task.Generation,
                    ProducerId = turnId,
                    TurnId = turnId,
                    IsExecutionBound = true,
                },
                new PendingSnapshots(
                    new ConversationMessageSnapshot
                    {
                        MessageId = messageId,
                        CreatedAt = _clock.GetUtcNow(),
                        StepIndex = 1,
                        Message = new ChatMessage(ChatRole.Assistant, "late") { MessageId = messageId.ToString("D") },
                        Metadata = [],
                    }
                ),
                changedBytes: 1,
                TestContext.Current.CancellationToken
            );
            await buffer.FlushAsync(TestContext.Current.CancellationToken);
        }
        catch (AgwException exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            await buffer.CompleteAsync(failure);
        }
    }

    /// <summary>
    /// 在带旧租约写入入口的执行作用域中记录 Agentflow 存档。
    /// Records an Agentflow checkpoint inside an execution scope carrying the old lease's write guard.
    /// </summary>
    private async Task RecordLateCheckpointAsync(AgentExecutionTask task, Guid turnId, IExecutionWriteGuard guard)
    {
        var context = ExecutionTestScopes.Context(
            task.ProjectId,
            task.ContextId,
            UserId,
            runtimeType: AgentRuntimeType.Agentflow,
            conversationId: task.ProjectConversationId
        ) with
        {
            TurnId = turnId,
        };
        using var scope = ExecutionScope
            .Create(
                context,
                task.TaskId,
                new InteractionTestSink(),
                new InMemoryPendingInteractionSet(null),
                writeGuard: guard
            )
            .Push();
        await new AgentflowCheckpointStore(
            _kit.Services.GetRequiredService<IServiceScopeFactory>(),
            _kit.Locks,
            _clock
        ).RecordAsync(
            turnId,
            task.ProjectId,
            task.ProjectConversationId,
            task.ContextId,
            task.TaskId,
            Guid.CreateVersion7(),
            UserId,
            isDurable: true,
            new string('a', 64),
            new DurableAgentflowCheckpoint
            {
                SessionId = "late",
                CheckpointId = "late",
                Payload = JsonSerializer.SerializeToElement(new { step = 1 }),
            },
            new Dictionary<string, string> { ["marker"] = "Marker" },
            TestContext.Current.CancellationToken
        );
    }

    private sealed class PendingSnapshots : IConversationMessageSource
    {
        private readonly ConversationMessageSnapshot _snapshot;
        private bool _acknowledged;

        public PendingSnapshots(ConversationMessageSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public IReadOnlyList<Guid> GetPendingMessageIds() => _acknowledged ? [] : [_snapshot.MessageId];

        public IReadOnlyList<ConversationMessageSnapshot> CapturePending() => _acknowledged ? [] : [_snapshot];

        public void Acknowledge(IReadOnlyList<ConversationMessageSnapshot> snapshots) =>
            _acknowledged = snapshots.Contains(_snapshot);
    }
}
