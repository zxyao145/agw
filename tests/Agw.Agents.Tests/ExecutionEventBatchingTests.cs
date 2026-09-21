using System.Data.Common;
using System.Threading.Channels;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Messaging.Durable;
using Agw.Agents.Execution.Outbound.Durable;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Infrastructure.Data;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Agw.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Tests;

public sealed partial class DurableExecutionStoreTests
{
    [Fact]
    public async Task BatchSink_Timer_FlushesWithoutAnotherEvent()
    {
        var clock = new ManualTimeProvider();
        var stream = new BatchRecordingStream();
        await using var sink = new ExecutionStreamMessageSink(
            stream,
            Guid.NewGuid(),
            0,
            NullLogger.Instance,
            timeProvider: clock
        );
        await sink.WriteAsync(BatchText("first"), TestContext.Current.CancellationToken);
        await clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMilliseconds(249));
        Assert.Empty(stream.Batches);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await stream.WaitAsync();

        Assert.Equal(0, Assert.Single(Assert.Single(stream.Batches)).Sequence);
    }

    [Fact]
    public async Task BatchSink_CapacityAndDispose_FlushInOrderAndSnapshotContent()
    {
        var stream = new BatchRecordingStream();
        var options = new ExecutionEventStreamOptions { WriteBatchSize = 2, WriteIntervalMilliseconds = 10000 };
        await using (var sink = new ExecutionStreamMessageSink(stream, Guid.NewGuid(), 2, NullLogger.Instance, options))
        {
            var message = BatchText("original");
            await sink.WriteAsync(message, TestContext.Current.CancellationToken);
            ((AgwTextContent)message.Contents[0]).Content = "mutated";
            await sink.WriteAsync(BatchText("second"), TestContext.Current.CancellationToken);
            Assert.Equal(2, Assert.Single(stream.Batches).Length);
            await sink.WriteAsync(BatchText("third"), TestContext.Current.CancellationToken);
        }

        Assert.Equal([2, 1], stream.Batches.Select(batch => batch.Length));
        Assert.Equal([0, 1, 2], stream.Batches.SelectMany(batch => batch).Select(entry => entry.Sequence));
        var snapshot = JsonUtil.Deserialize<AgwMessage>(stream.Batches[0][0].PayloadJson)!;
        Assert.Equal("original", ((AgwTextContent)snapshot.Contents[0]).Content);
    }

    [Fact]
    public async Task BatchSink_SlowStore_AppliesBackpressure()
    {
        var stream = new BatchRecordingStream();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.BeforeWrite = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        await using var sink = new ExecutionStreamMessageSink(
            stream,
            Guid.NewGuid(),
            0,
            NullLogger.Instance,
            new ExecutionEventStreamOptions { WriteBatchSize = 2, WriteIntervalMilliseconds = 10000 }
        );
        await sink.WriteAsync(BatchText("1"), TestContext.Current.CancellationToken);
        var second = sink.WriteAsync(BatchText("2"), TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var third = sink.WriteAsync(BatchText("3"), TestContext.Current.CancellationToken).AsTask();
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        release.SetResult();
        await Task.WhenAll(second, third);

        Assert.Equal(2, Assert.Single(stream.Batches).Length);
    }

    [Fact]
    public async Task BatchSink_InvalidatedAttempt_DoesNotFlushPendingEvents()
    {
        using var invalidated = new CancellationTokenSource();
        var stream = new BatchRecordingStream();
        await using (
            var sink = new ExecutionStreamMessageSink(
                stream,
                Guid.NewGuid(),
                0,
                NullLogger.Instance,
                new ExecutionEventStreamOptions { WriteIntervalMilliseconds = 10000 },
                invalidated: invalidated.Token
            )
        )
        {
            await sink.WriteAsync(BatchText("stale"), TestContext.Current.CancellationToken);
            invalidated.Cancel();
        }

        Assert.Empty(stream.Batches);
    }

    [Fact]
    public async Task BatchSink_UnavailableReplay_DisablesFurtherWritesWithoutFailingExecution()
    {
        var stream = new BatchRecordingStream
        {
            BeforeWrite = _ => throw new AgwException(ErrorCodes.DurableExecutionUnavailable),
        };
        await using var sink = new ExecutionStreamMessageSink(
            stream,
            Guid.NewGuid(),
            0,
            NullLogger.Instance,
            new ExecutionEventStreamOptions { WriteIntervalMilliseconds = 0 }
        );
        await sink.WriteAsync(BatchText("first"), TestContext.Current.CancellationToken);
        await sink.WriteAsync(BatchText("second"), TestContext.Current.CancellationToken);
        await sink.DisposeAsync();

        Assert.Equal(1, stream.Attempts);
        Assert.Empty(stream.Batches);
    }

    [Fact]
    public async Task BatchSink_Finished_DefersControlMessageAndDrainsOutputOnDisposal()
    {
        // Arrange
        var stream = new BatchRecordingStream();
        await using var sink = new ExecutionStreamMessageSink(
            stream,
            Guid.NewGuid(),
            0,
            NullLogger.Instance,
            timeProvider: new ManualTimeProvider()
        );

        // Act
        await sink.WriteAsync(BatchText("tail"), TestContext.Current.CancellationToken);
        await sink.WriteAsync(TurnMessageFactory.CreateFinished("completed"), TestContext.Current.CancellationToken);
        Assert.Empty(stream.Batches);
        await sink.DisposeAsync();

        // Assert
        var entry = Assert.Single(Assert.Single(stream.Batches));
        Assert.Equal(0, entry.Sequence);
        Assert.False(entry.IsTerminal);
        Assert.Equal(
            "tail",
            ((AgwTextContent)JsonUtil.Deserialize<AgwMessage>(entry.PayloadJson)!.Contents[0]).Content
        );
    }

    [Fact]
    public async Task PostgresBatch_OverlappingReplay_WritesOnlyMissingPositionsAndKeepsEncryption()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync();
        var counter = new EventTransactionCounter();
        using var provider = database.CreateServiceProvider(counter);
        var stream = new PostgresExecutionEventStream(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ExecutionRuntimeOptions())
        );
        var id = await RegisterExecutionAsync(database, database.CreateStore());
        await stream.AppendBatchAsync(
            id,
            0,
            [new(0, BatchText("secret-first")), new(1, BatchText("secret-second"))],
            token
        );
        Assert.Equal(1, counter.Commits);
        await stream.AppendBatchAsync(
            id,
            0,
            [new(1, BatchText("replacement")), new(2, BatchText("third")), new(3, BatchText("fourth"))],
            token
        );

        var records = await stream.ReadAsync(id, null, token);
        Assert.Equal(["1-0", "1-1", "1-2", "1-3"], records.Select(record => record.Cursor));
        Assert.Equal("secret-second", ((AgwTextContent)records[1].Message.Contents[0]).Content);
        await using var command = database.Context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT payload_json FROM execution_stream_entry LIMIT 1";
        var raw = Assert.IsType<string>(await command.ExecuteScalarAsync(token));
        Assert.DoesNotContain("secret", raw);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public async Task PostgresBatch_WrappedTransientFailure_RetriesWithFixedBudget(int failures)
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync();
        var id = await RegisterExecutionAsync(database, database.CreateStore());
        var interceptor = new TransientSaveFailure(failures);
        using var services = database.CreateServiceProvider(interceptor);
        var stream = new PostgresExecutionEventStream(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ExecutionRuntimeOptions())
        );

        if (failures == 1)
        {
            await stream.AppendAsync(id, 0, 0, BatchText("retry once"), token);
            Assert.Single(await stream.ReadAsync(id, null, token));
            Assert.Equal(2, interceptor.Attempts);
        }
        else
        {
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                stream.AppendAsync(id, 0, 0, BatchText("unavailable"), token).AsTask()
            );
            Assert.Equal(ErrorCodes.DurableExecutionUnavailable.Code, error.Code);
            Assert.Equal(6, interceptor.Attempts);
            Assert.Empty(await stream.ReadAsync(id, null, token));
        }
    }

    private sealed class TransientSaveFailure : SaveChangesInterceptor
    {
        private readonly int _failures;
        public int Attempts { get; private set; }

        public TransientSaveFailure(int failures)
        {
            _failures = failures;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (++Attempts <= _failures)
                throw new InvalidOperationException(
                    "Provider wrapper",
                    new DbUpdateException("Batch rolled back", new TransientDatabaseFailure())
                );
            return ValueTask.FromResult(result);
        }

        private sealed class TransientDatabaseFailure : DbException
        {
            public override bool IsTransient => true;
        }
    }

    [Fact]
    public async Task Coordinator_TerminalRacesFirstRead_DrainsCommittedTailBeforeFinished()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var id = await RegisterExecutionAsync(database, store);
        var claim = await store.TryBeginSegmentAsync(id, DateTimeOffset.MaxValue, token);
        Assert.NotNull(claim);
        var stream = new BatchRecordingStream();
        var reads = 0;
        stream.OnRead = async cursor =>
        {
            if (++reads == 1)
            {
                await store.SaveSegmentResultAsync(
                    new DurableExecutionSegmentResult
                    {
                        ExecutionId = id,
                        SegmentIndex = 0,
                        Status = DurableExecutionSegmentStatus.Completed,
                    },
                    claim.StateVersion,
                    token
                );
                return [];
            }
            return cursor == null ? [new ExecutionStreamEntry("1-0", BatchText("last output"))] : [];
        };
        var services = new ServiceCollection();
        services.AddScoped<IAgentsDbContext>(_ => database.CreateContext());
        services.AddScoped(sp =>
        {
            var db = (AgwDbContext)sp.GetRequiredService<IAgentsDbContext>();
            return new DurableExecutionStore(
                db,
                TimeProvider.System,
                InMemoryApplicationLock.Shared,
                TestDurablePersistence.Create(db)
            );
        });
        await using var provider = services.BuildServiceProvider();
        var coordinator = new DurableExecutionCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            InMemoryApplicationLock.Shared,
            stream,
            TimeProvider.System,
            Options.Create(new ExecutionRuntimeOptions()),
            NullLogger<DurableExecutionCoordinator>.Instance
        );
        var observed = new List<ExecutionStreamEntry>();
        await foreach (var entry in coordinator.ReadAsync(id, "user-id", null, token))
            observed.Add(entry);

        Assert.Equal(2, observed.Count);
        Assert.Equal("last output", ((AgwTextContent)observed[0].Message.Contents[0]).Content);
        Assert.True(TurnMessageProtocol.IsFinished(observed[1].Message));
    }

    private static AgwMessage BatchText(string text) =>
        new(Guid.NewGuid().ToString(), "agent", AiRole.Assistant, [new AgwTextContent { Content = text }]);

    private sealed class EventTransactionCounter : DbTransactionInterceptor
    {
        public int Commits { get; private set; }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default
        )
        {
            Commits++;
            return Task.CompletedTask;
        }
    }

    private sealed class BatchRecordingStream : IExecutionEventStream
    {
        private readonly Channel<int> _writes = Channel.CreateUnbounded<int>();
        private readonly List<ExecutionStreamWrite[]> _batches = [];
        public ExecutionStreamWrite[][] Batches
        {
            get
            {
                lock (_batches)
                    return _batches.ToArray();
            }
        }
        public Func<CancellationToken, Task>? BeforeWrite { get; set; }
        public Func<string?, Task<IReadOnlyList<ExecutionStreamEntry>>>? OnRead { get; set; }
        public int Attempts { get; private set; }

        public ValueTask AppendAsync(
            Guid executionId,
            int segmentIndex,
            int sequence,
            AgwMessage message,
            CancellationToken token
        ) => AppendBatchAsync(executionId, segmentIndex, [new(sequence, message)], token);

        public async ValueTask AppendBatchAsync(
            Guid executionId,
            int segmentIndex,
            IReadOnlyList<ExecutionStreamWrite> messages,
            CancellationToken token
        )
        {
            Attempts++;
            if (BeforeWrite != null)
                await BeforeWrite(token);
            lock (_batches)
                _batches.Add(messages.ToArray());
            _writes.Writer.TryWrite(messages.Count);
        }

        public Task<IReadOnlyList<ExecutionStreamEntry>> ReadAsync(
            Guid executionId,
            string? cursor,
            CancellationToken token
        ) => OnRead?.Invoke(cursor) ?? Task.FromResult<IReadOnlyList<ExecutionStreamEntry>>([]);

        public Task<int> WaitAsync() =>
            _writes
                .Reader.ReadAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
