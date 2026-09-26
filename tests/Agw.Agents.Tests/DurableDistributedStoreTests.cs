using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Messaging.Durable;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Agw.Agents.Tests;

/// <summary>
/// 生产 Durable 路径在真实 PostgreSQL 与 Redis 上的行为：数据库时间、行锁与投影。需要隔离的测试服务器，未配置时跳过。
/// The production durable path on real PostgreSQL and Redis: database time, row locks and the projection. Requires isolated test servers and is skipped when they are not configured.
/// </summary>
public sealed class DurableDistributedStoreTests : IDisposable
{
    private const string PostgresVariable = "AGW_TEST_POSTGRES_CONNECTION_STRING";
    private const string RedisVariable = "AGW_TEST_REDIS_CONNECTION_STRING";

    private readonly IDisposable _user = TurnPersistenceTestKit.EnterUser();

    public static bool PostgresEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresVariable));

    public static bool RedisEnabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RedisVariable));

    public void Dispose() => _user.Dispose();

    [Fact(
        SkipUnless = nameof(PostgresEnabled),
        Skip = "Set AGW_TEST_POSTGRES_CONNECTION_STRING to an isolated PostgreSQL server with CREATE DATABASE permission."
    )]
    public async Task Postgres_ConcurrentCommits_AssignContiguousSequences()
    {
        // Arrange
        await using var kit = await TurnPersistenceTestKit.CreatePostgresAsync(
            Environment.GetEnvironmentVariable(PostgresVariable)!
        );
        var id = await AcceptQueuedAsync(kit);
        var lease = await ClaimAsync(kit, id, "worker-a", TimeSpan.FromSeconds(30));
        using var ownership = new CancellationTokenSource();
        var guard = kit.Leases.CreateGuard(lease, ownership);

        // Act
        var committed = await Task.WhenAll(
            Enumerable
                .Range(0, 32)
                .Select(index => Task.Run(() => AppendAsync(guard, id, lease.Epoch, $"event-{index}")))
        );

        // Assert
        Assert.Equal(
            Enumerable.Range(2, 32).Select(value => (long)value),
            committed.Select(entries => Assert.Single(entries).Sequence).Order()
        );
        Assert.Equal(33, (await kit.ReadExecutionAsync(id)).LastEventSequence);
        Assert.False(ownership.IsCancellationRequested);
    }

    [Fact(
        SkipUnless = nameof(PostgresEnabled),
        Skip = "Set AGW_TEST_POSTGRES_CONNECTION_STRING to an isolated PostgreSQL server with CREATE DATABASE permission."
    )]
    public async Task Postgres_ExpiredLease_TakeoverRejectsOldInstanceWrites()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var kit = await TurnPersistenceTestKit.CreatePostgresAsync(
            Environment.GetEnvironmentVariable(PostgresVariable)!
        );
        var id = await AcceptQueuedAsync(kit);
        var oldLease = await ClaimAsync(kit, id, "worker-a", TimeSpan.FromSeconds(1));
        while (!(await kit.Leases.GetClaimableAsync(10, token)).Contains(id))
            await Task.Delay(TimeSpan.FromMilliseconds(200), token);
        var newLease = await ClaimAsync(kit, id, "worker-b", TimeSpan.FromSeconds(30));
        using var oldOwnership = new CancellationTokenSource();
        var oldGuard = kit.Leases.CreateGuard(oldLease, oldOwnership);

        // Act
        var eventError = await Assert.ThrowsAsync<AgwException>(() =>
            AppendAsync(oldGuard, id, oldLease.Epoch, "late")
        );
        var resultError = await Assert.ThrowsAsync<AgwException>(() =>
            oldGuard.RunAsync(
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
            )
        );

        // Assert
        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, eventError.Code);
        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, resultError.Code);
        Assert.True(oldOwnership.IsCancellationRequested);
        Assert.Equal(oldLease.Epoch + 1, newLease.Epoch);
        var record = await kit.ReadExecutionAsync(id);
        Assert.Equal(DurableExecutionStatus.Running, record.Status);
        Assert.Equal("worker-b", record.WorkerId);
        Assert.Equal(1, record.LastEventSequence);
        using var newOwnership = new CancellationTokenSource();
        var current = await AppendAsync(kit.Leases.CreateGuard(newLease, newOwnership), id, newLease.Epoch, "current");
        Assert.Equal(2, Assert.Single(current).Sequence);
    }

    [Fact(
        SkipUnless = nameof(RedisEnabled),
        Skip = "Set AGW_TEST_REDIS_CONNECTION_STRING to an isolated Redis server."
    )]
    public async Task Redis_Projection_FillsGapsFromCommittedEventsAndReadsContiguously()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var redis = await ConnectionMultiplexer.ConnectAsync(
            Environment.GetEnvironmentVariable(RedisVariable)!
        );
        await using var kit = await TurnPersistenceTestKit.CreateAsync(
            executionOptions: new ExecutionRuntimeOptions
            {
                Provider = ExecutionProvider.Distributed,
                Distributed = new DistributedExecutionOptions
                {
                    EventStream = new ExecutionEventStreamOptions { Provider = ExecutionEventStreamProvider.Redis },
                },
            },
            configure: services =>
            {
                services.AddSingleton<IConnectionMultiplexer>(redis);
                services.AddSingleton<RedisExecutionEventProjection>();
            }
        );
        var id = await AcceptQueuedAsync(kit);
        var key = new RedisKey($"agw:turn:{id:N}:events");
        try
        {
            var lease = await ClaimAsync(kit, id, "worker-a", TimeSpan.FromSeconds(30));
            using var ownership = new CancellationTokenSource();
            var guard = kit.Leases.CreateGuard(lease, ownership);
            await AppendAsync(guard, id, lease.Epoch, "second");
            var latest = await AppendAsync(guard, id, lease.Epoch, "third");
            var eventLog = kit.Services.GetRequiredService<DurableExecutionEventLog>();
            var projection = kit.Services.GetRequiredService<RedisExecutionEventProjection>();

            // Act
            await eventLog.PublishAsync(id, latest, token);
            await eventLog.PublishAsync(id, latest, token);

            // Assert
            Assert.Equal(3, await projection.GetLastSequenceAsync(id, token));
            Assert.Equal([1L, 2L, 3L], (await projection.ReadAsync(id, 0, token)).Select(entry => entry.Sequence));
            Assert.Equal(3, await redis.GetDatabase().StreamLengthAsync(key));
            var read = await eventLog.ReadAsync(id, 1, token);
            Assert.Equal([2L, 3L], read.Select(entry => entry.Sequence));
            Assert.Equal(
                ["second", "third"],
                read.Select(entry => Assert.IsType<AgwTextContent>(Assert.Single(entry.Message.Contents)).Content)
            );
        }
        finally
        {
            await redis.GetDatabase().KeyDeleteAsync(key);
        }
    }

    private static async Task<Guid> AcceptQueuedAsync(TurnPersistenceTestKit kit)
    {
        var task = await kit.SeedConversationAsync();
        var accepted = await kit.CreateAcceptance(
                projectTasks: null,
                new AgentExecutionFacadeTests.WorkspaceProjects(),
                kit.Coordinator
            )
            .AcceptAsync(
                new TurnAcceptanceRequest(
                    TurnPersistenceTestKit.UserId,
                    TurnId: null,
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
        return accepted.Request.TurnId;
    }

    private static async Task<DurableLease> ClaimAsync(
        TurnPersistenceTestKit kit,
        Guid executionId,
        string workerId,
        TimeSpan duration
    ) =>
        Assert.IsType<DurableLease>(
            await kit.Leases.TryClaimAsync(executionId, workerId, duration, TestContext.Current.CancellationToken)
        );

    private static Task<IReadOnlyList<TurnBroadcastEntry>> AppendAsync(
        IExecutionWriteGuard guard,
        Guid turnId,
        long leaseEpoch,
        string text
    ) =>
        guard.RunAsync(
            (services, token) =>
                DurableExecutionEvents.AppendAsync(
                    services.GetRequiredService<IAgentsDbContext>(),
                    services.GetRequiredService<IDurableExecutionEventSequence>(),
                    turnId,
                    leaseEpoch,
                    0,
                    [
                        PendingExecutionEvent.Create(
                            new AgwMessage(
                                Guid.CreateVersion7().ToString("D"),
                                "agent",
                                AiRole.Assistant,
                                [new AgwTextContent { Content = text }]
                            )
                        ),
                    ],
                    lookupCommitted: false,
                    token
                ),
            TestContext.Current.CancellationToken
        );
}
