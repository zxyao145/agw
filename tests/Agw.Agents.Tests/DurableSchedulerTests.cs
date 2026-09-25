using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Contracts.History;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Tests;

public sealed class DurableSchedulerTests
{
    public static bool PostgresEnabled => DurableTurnUpgradeTests.PostgresEnabled;

    [Theory(
        SkipUnless = nameof(PostgresEnabled),
        Skip = "Requires an isolated PostgreSQL server with CREATE DATABASE permission."
    )]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Postgres_ConcurrentStarts_ReserveCapacityBeforeLeasesAndReleaseOnFailure(int limit)
    {
        // 准备真实数据库行锁，让领取操作等待。
        // Arrange real database row locks that keep lease claims waiting.
        var token = TestContext.Current.CancellationToken;
        using var owner = TurnPersistenceTestKit.EnterUser();
        var options = new ExecutionRuntimeOptions
        {
            Distributed = new DistributedExecutionOptions { MaxConcurrentExecutions = limit },
        };
        await using var kit = await TurnPersistenceTestKit.CreatePostgresAsync(
            Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING")!,
            options
        );
        using var host = Host.CreateApplicationBuilder().Build();
        var scheduler = new DurableSegmentScheduler(
            kit.Services,
            host.Services.GetRequiredService<IHostApplicationLifetime>(),
            NullLogger<DurableSegmentScheduler>.Instance,
            kit.Leases,
            new DurableWorkerIdentity(),
            Options.Create(options)
        );
        var requests = new List<ExecutionRequest>();
        for (var index = 0; index < limit + 2; index++)
            requests.Add(await AcceptAsync(kit, index));

        await using var locked = kit.CreateContext();
        await using var transaction = await locked.Database.BeginTransactionAsync(token);
        await locked.DurableExecutions.ExecuteUpdateAsync(
            setters => setters.SetProperty(record => record.LeaseEpoch, record => record.LeaseEpoch),
            token
        );

        // 同一本地入口与 Worker 使用的调度方法并发领取。
        // Act through the shared local and worker scheduling entry.
        var starts = requests.Select(request => scheduler.TryStartAsync(request.TurnId, token)).ToArray();
        try
        {
            Assert.Equal(limit, scheduler.RunningCount);
            Assert.All(await Task.WhenAll(starts.Skip(limit)), started => Assert.False(started));
            await using var observer = kit.CreateContext();
            Assert.All(
                await observer.DurableExecutions.ToArrayAsync(token),
                record =>
                {
                    Assert.Equal(DurableExecutionStatus.Queued, record.Status);
                    Assert.Null(record.WorkerId);
                    Assert.Null(record.LeaseExpiresAt);
                }
            );
        }
        finally
        {
            await transaction.CommitAsync(token);
            await Task.WhenAll(starts).WaitAsync(TimeSpan.FromSeconds(15), token);
            await scheduler.WaitAllAsync().WaitAsync(TimeSpan.FromSeconds(15), token);
        }

        // 此环境没有模型运行服务，执行失败后名额必须释放。
        // Assert execution releases capacity when model runtime services are unavailable.
        Assert.Equal(0, scheduler.RunningCount);
        Assert.All(await Task.WhenAll(starts.Take(limit)), started => Assert.True(started));
        Assert.True(await scheduler.TryStartAsync(requests[^1].TurnId, token));
        await scheduler.WaitAllAsync().WaitAsync(TimeSpan.FromSeconds(15), token);
        Assert.Equal(0, scheduler.RunningCount);
    }

    private static async Task<ExecutionRequest> AcceptAsync(TurnPersistenceTestKit kit, int index)
    {
        var token = TestContext.Current.CancellationToken;
        var task = await kit.SeedConversationAsync(contextId: $"scheduler-{index}");
        var id = Guid.CreateVersion7();
        var target = new ExecutionTarget(Guid.CreateVersion7(), AgentRuntimeType.Agent);
        var request = new ExecutionRequest(
            id,
            TurnPersistenceTestKit.UserId,
            target,
            task,
            TurnPersistenceTestKit.CreateSettings(task.ProjectId, task.ContextId),
            new AgwUserInput { Contents = [] },
            true,
            ProjectWorkspacePaths.CreateSnapshot(task.ProjectId, AppContext.BaseDirectory, [])
        )
        {
            Envelope = TurnPersistenceTestKit.CreateEnvelope(id, task, target),
        };
        await kit.ResolveScoped<ITurnAcceptanceWriter>()
            .AcceptAsync(
                new TurnAcceptanceWrite
                {
                    Turn = new AcceptConversationTurnRequest
                    {
                        TurnId = id,
                        ProjectId = task.ProjectId,
                        ConversationId = task.ProjectConversationId,
                        ContextId = task.ContextId,
                        Generation = 0,
                        TargetId = target.AgentId,
                        TargetType = ConversationTurnTargetType.Agent,
                    },
                    Durable = kit.Coordinator.CreateRegistration(
                        request,
                        TurnMessageFactory.CreateStarted(request.Envelope)
                    ),
                },
                token
            );
        return request;
    }
}
