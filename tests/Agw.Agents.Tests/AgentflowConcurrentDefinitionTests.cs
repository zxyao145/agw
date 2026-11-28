using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed partial class AgentflowAppServiceTests
{
    [Fact]
    public async Task SaveRevisionAsync_ConcurrentSameBaseRevision_OnlyOneRevisionCommits()
    {
        var token = TestContext.Current.CancellationToken;
        using var caller = UserInfoUtil.Push(
            new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "tester")],
                    "test"
                )
            )
        );
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"agentflow-concurrent-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
        // Keep the shared database alive while each concurrent context uses its own connection.
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(token);
        await using var seed = await CreateDbContextAsync(connection);
        var userInfo = new TestUserInfoService("tester");
        var agent = await SeedAgentAsync(seed, "Runner", "tester");
        var initial = await CreateService(seed, userInfo).CreateAsync("flow", null, CreateDefinition(agent.Id), token);
        seed.ChangeTracker.Clear();
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AgwException?> SaveRevisionAsync(string name)
        {
            await start.Task;
            await using var db = new AgwDbContext(options);
            var service = CreateService(db, new TestUserInfoService("tester"));
            try
            {
                await service.SaveRevisionAsync(initial.Id, 1, name, null, CreateDefinition(agent.Id), token);
                return null;
            }
            catch (AgwException exception)
            {
                return exception;
            }
        }
        var firstUpdate = Task.Run(() => SaveRevisionAsync("first-update"), token);
        var secondUpdate = Task.Run(() => SaveRevisionAsync("second-update"), token);
        start.SetResult();

        var results = await Task.WhenAll(firstUpdate, secondUpdate);

        Assert.Single(results, result => result is null);
        Assert.Equal(
            ErrorCodes.AgentflowRevisionConflict.Code,
            Assert.Single(results, result => result is not null)!.Code
        );
        Assert.Equal(2, await seed.AgentflowRevisions.CountAsync(row => row.AgentflowId == initial.Id, token));
        Assert.Equal(
            2,
            (await seed.Agentflows.AsNoTracking().SingleAsync(row => row.Id == initial.Id, token)).LatestRevision
        );
    }

    [Fact]
    public async Task SaveRevisionAsync_LaterLockWinnerMakesQueuedStaleSaveConflictBeforeValidation()
    {
        var token = TestContext.Current.CancellationToken;
        using var caller = UserInfoUtil.Push(
            new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "tester")],
                    "test"
                )
            )
        );
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"agentflow-ordered-concurrent-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(token);
        await using var seed = await CreateDbContextAsync(connection);
        var userInfo = new TestUserInfoService("tester");
        var agent = await SeedAgentAsync(seed, "Runner", "tester");
        var initial = await CreateService(seed, userInfo).CreateAsync("flow", null, CreateDefinition(agent.Id), token);
        seed.ChangeTracker.Clear();
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        var applicationLock = new FirstAcquireBlockingApplicationLock();

        async Task<AgwException?> SaveRevisionAsync(string name)
        {
            await using var db = new AgwDbContext(options);
            try
            {
                await CreateService(db, new TestUserInfoService("tester"), applicationLock)
                    .SaveRevisionAsync(initial.Id, 1, name, null, CreateDefinition(agent.Id), token);
                return null;
            }
            catch (AgwException exception)
            {
                return exception;
            }
        }

        var staleSave = SaveRevisionAsync(" ");
        await applicationLock.FirstAcquireEntered.WaitAsync(token);
        var laterSave = await SaveRevisionAsync("later-save");
        applicationLock.ReleaseFirstAcquire();
        var staleResult = await staleSave;

        Assert.Null(laterSave);
        Assert.Equal(ErrorCodes.AgentflowRevisionConflict.Code, staleResult?.Code);
        Assert.Equal(2, await seed.AgentflowRevisions.CountAsync(row => row.AgentflowId == initial.Id, token));
        Assert.Equal(
            2,
            (await seed.Agentflows.AsNoTracking().SingleAsync(row => row.Id == initial.Id, token)).LatestRevision
        );
    }

    private sealed class FirstAcquireBlockingApplicationLock : IApplicationLock
    {
        private readonly InMemoryApplicationLock _inner = new();
        private readonly TaskCompletionSource _firstAcquireEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _releaseFirstAcquire = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _acquireCount;

        public Task FirstAcquireEntered => _firstAcquireEntered.Task;

        public void ReleaseFirstAcquire() => _releaseFirstAcquire.TrySetResult();

        public async Task<IApplicationLockLease> AcquireAsync(string resourceName, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _acquireCount) == 1)
            {
                _firstAcquireEntered.TrySetResult();
                await _releaseFirstAcquire.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await _inner.AcquireAsync(resourceName, cancellationToken).ConfigureAwait(false);
        }

        public Task<IApplicationLockLease> AcquireSharedAsync(
            string resourceName,
            CancellationToken cancellationToken
        ) => _inner.AcquireSharedAsync(resourceName, cancellationToken);

        public Task<IApplicationLockLease?> TryAcquireExclusiveAsync(
            string resourceName,
            CancellationToken cancellationToken
        ) => _inner.TryAcquireExclusiveAsync(resourceName, cancellationToken);
    }
}
