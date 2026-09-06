using Agw.Agents.Execution.Durable;
using Agw.Shared.Configuration;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Tests;

public sealed partial class DurableExecutionStoreTests
{
    [Fact]
    public async Task Worker_LosesLease_CancelsSegmentAndRejectsItsLateCompletion()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync();
        var executionId = await RegisterExecutionAsync(database, database.CreateStore());
        var locks = new LosingExecutionLock();
        var executor = new CanceledSegmentExecutor();
        var stream = new RecordingExecutionEventStream();
        var services = new ServiceCollection();
        services.AddScoped(_ => database.CreateContext());
        services.AddScoped(provider => new DurableExecutionStore(
            provider.GetRequiredService<Agw.Infrastructure.Data.AgwDbContext>(),
            TimeProvider.System,
            locks,
            TestDurablePersistence.Create(provider.GetRequiredService<Agw.Infrastructure.Data.AgwDbContext>())
        ));
        services.AddSingleton<IDurableExecutionSegmentExecutor>(executor);
        await using var provider = services.BuildServiceProvider();
        using var worker = new DistributedExecutionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            locks,
            stream,
            new WorkerInitializationState(),
            TimeProvider.System,
            Options.Create(new ExecutionRuntimeOptions()),
            NullLogger<DistributedExecutionWorker>.Instance
        );

        await worker.StartAsync(token);
        try
        {
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            locks.LoseLease();
            await executor.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        }
        finally
        {
            await worker.StopAsync(token);
        }

        Assert.Equal(
            DurableExecutionStatus.Running,
            (await database.CreateStore().GetAsync(executionId, token)).Status
        );
        Assert.DoesNotContain(stream.Appends, append => append.Sequence == int.MaxValue);
    }

    private sealed class CanceledSegmentExecutor : IDurableExecutionSegmentExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DurableExecutionSegmentResult> RunAsync(
            DurableExecutionSegmentInput input,
            CancellationToken cancellationToken
        )
        {
            using var registration = cancellationToken.Register(() => Canceled.TrySetResult());
            Started.TrySetResult();
            await Canceled.Task;
            // An SDK may still return a result after cancellation; the worker must fence this result.
            return new DurableExecutionSegmentResult
            {
                ExecutionId = input.ExecutionId,
                SegmentIndex = input.SegmentIndex,
                Status = DurableExecutionSegmentStatus.Completed,
            };
        }
    }

    private sealed class LosingExecutionLock : IApplicationLock
    {
        private readonly InMemoryApplicationLock _inner = new();
        private readonly CancellationTokenSource _lost = new();

        public void LoseLease() => _lost.Cancel();

        public async Task<IApplicationLockLease> AcquireAsync(string resourceName, CancellationToken cancellationToken)
        {
            var lease = await _inner.AcquireAsync(resourceName, cancellationToken);
            return resourceName.StartsWith("distributed-execution:", StringComparison.Ordinal)
                ? new Lease(lease, _lost.Token)
                : lease;
        }

        private sealed class Lease : IApplicationLockLease
        {
            private readonly IApplicationLockLease _inner;

            public Lease(IApplicationLockLease inner, CancellationToken lost)
            {
                _inner = inner;
                HandleLostToken = lost;
            }

            public CancellationToken HandleLostToken { get; }

            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }
    }

    private sealed class WorkerInitializationState : IServerInitializationState
    {
        public bool IsInitialized => true;
        public DatabaseProvider DatabaseProvider => DatabaseProvider.Sqlite;
        public string DatabaseConnectionString => "Data Source=:memory:";
    }
}
