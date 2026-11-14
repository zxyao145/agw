using System.Data.Common;
using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Events.Contracts;
using Agw.Infrastructure.Data;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs.Tests;

public sealed class JobsPostgresTests
{
    private const string Owner = "jobs-postgres-test-user";

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Update_ProjectDeletedAfterJobRead_ThrowsConflictAndLeavesNoJobRecords()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await CreateDatabaseAsync(cancellationToken);
        var jobRead = new JobReadGate();
        await using var host = await CreateHostAsync(database, jobRead);
        using var user = PushUser();

        var projectId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        var job = await CreateJobAsync(host, projectId, agentId, cancellationToken);
        var sourceId = await GetSourceIdAsync(host.Services, job.Id, cancellationToken);
        var callerKey = $"source:{sourceId}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var updateScope = host.Services.CreateAsyncScope();
        var jobs = updateScope.ServiceProvider.GetRequiredService<IEventJobFacade>();
        var update = jobs.UpdateAsync(job.Id, job.Definition with { Name = "Updated job" }, timeout.Token);
        try
        {
            var read = jobRead.WaitAsync(timeout.Token);
            if (await Task.WhenAny(read, update) == update)
                await update;
            await read;
            await using var definitionLease = await host
                .Services.GetRequiredService<IApplicationLock>()
                .AcquireAsync(AgentDefinitionLock.GetResourceName(Owner), timeout.Token);
            await using var deleteScope = host.Services.CreateAsyncScope();
            var projects = deleteScope.ServiceProvider.GetRequiredService<IProjectDeletionCoordinator>();
            Assert.True(await projects.DeleteProjectAsync(new ProjectDeletionTarget(projectId, Owner), timeout.Token));
        }
        finally
        {
            jobRead.Release();
        }

        var exception = await Assert.ThrowsAsync<AgwException>(() => update);
        Assert.Equal(ErrorCodes.JobModificationConflict.Code, exception.Code);

        await using var verificationScope = host.Services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<AgwDbContext>();
        Assert.False(await db.EventSubscriptions.AnyAsync(row => row.ProjectId == projectId, cancellationToken));
        var jobKey = job.Id.ToString("D");
        Assert.False(
            await db.EventSources.AnyAsync(row => row.ManagedKey == jobKey && row.DeletedAt == null, cancellationToken)
        );
        Assert.False(
            await db.EventSchedules.AnyAsync(
                row => row.CallerKey == callerKey && row.State == EventScheduleState.Active,
                cancellationToken
            )
        );
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Enable_ConcurrentRequests_KeepTimerSubscriptionAndScheduleConsistent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await CreateDatabaseAsync(cancellationToken);
        await using var host = await CreateHostAsync(database);
        using var user = PushUser();

        var job = await CreateJobAsync(host, Guid.CreateVersion7(), Guid.CreateVersion7(), cancellationToken);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable
            .Range(0, 16)
            .Select(async index =>
            {
                await start.Task.WaitAsync(cancellationToken);
                await using var scope = host.Services.CreateAsyncScope();
                return await scope
                    .ServiceProvider.GetRequiredService<IEventJobFacade>()
                    .SetEnabledAsync(job.Id, index % 2 == 0, cancellationToken);
            })
            .ToArray();

        start.SetResult(true);
        await Task.WhenAll(requests);

        await using var verificationScope = host.Services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var jobKey = job.Id.ToString("D");
        var source = await db.EventSources.SingleAsync(
            row => row.ManagedKey == jobKey && row.DeletedAt == null,
            cancellationToken
        );
        var subscription = await db.EventSubscriptions.SingleAsync(row => row.ManagedKey == jobKey, cancellationToken);
        var schedule = await db.EventSchedules.SingleAsync(
            row => row.CallerKey == $"source:{source.Id}",
            cancellationToken
        );

        Assert.Equal(source.Enabled, subscription.Enabled);
        Assert.Equal(source.Enabled, schedule.State == EventScheduleState.Active);
    }

    private static async Task<PostgresJobTestDatabase> CreateDatabaseAsync(CancellationToken cancellationToken) =>
        await PostgresJobTestDatabase.CreateAsync(
            PostgresJobTestDatabase.RequireAdminConnectionString(),
            cancellationToken
        );

    private static Task<ModuleCompositionTestHost> CreateHostAsync(
        PostgresJobTestDatabase database,
        IInterceptor? databaseInterceptor = null
    ) =>
        ModuleCompositionTestHost.CreateAsync(
            settings: PostgresSettings(database),
            configureServices: services =>
            {
                if (databaseInterceptor is not null)
                    services.ConfigureDbContext<AgwDbContext>(options => options.AddInterceptors(databaseInterceptor));
            }
        );

    internal static IReadOnlyDictionary<string, string?> PostgresSettings(PostgresJobTestDatabase database) =>
        new Dictionary<string, string?>
        {
            ["Database:Provider"] = "postgres",
            ["Database:ConnectionString"] = database.ConnectionString,
            ["Execution:Provider"] = "InProcess",
            ["DistributedLock:Provider"] = "postgres",
            ["DistributedLock:ConnectionString"] = database.ConnectionString,
        };

    private static async Task<EventJob> CreateJobAsync(
        ModuleCompositionTestHost host,
        Guid projectId,
        Guid agentId,
        CancellationToken cancellationToken
    )
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        db.Projects.Add(
            new Project
            {
                Id = projectId,
                Name = "Jobs PostgreSQL test project",
                Workspace = "jobs-postgres-test-workspace",
            }
        );
        db.Agents.Add(
            new Agent
            {
                Id = agentId,
                Name = "jobs-postgres-test-agent",
                DisplayName = "Jobs PostgreSQL test agent",
            }
        );
        await db.SaveChangesAsync(cancellationToken);

        return await scope
            .ServiceProvider.GetRequiredService<IEventJobFacade>()
            .CreateAsync(
                new EventJobDefinition
                {
                    Name = "PostgreSQL test job",
                    Enabled = true,
                    ProjectId = projectId,
                    TargetKind = EventJobTargetKind.Agent,
                    AgentId = agentId,
                    Instructions = "Run the PostgreSQL job test.",
                    Schedule = new EventJobSchedule { Kind = "Interval", Interval = "PT5M" },
                },
                cancellationToken
            );
    }

    private static async Task<Guid> GetSourceIdAsync(IServiceProvider services, Guid jobId, CancellationToken token)
    {
        await using var scope = services.CreateAsyncScope();
        var jobKey = jobId.ToString("D");
        return await scope
            .ServiceProvider.GetRequiredService<AgwDbContext>()
            .EventSources.Where(row => row.ManagedKey == jobKey)
            .Select(row => row.Id)
            .SingleAsync(token);
    }

    private static IDisposable PushUser() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Owner)], authenticationType: "Test")
            )
        );

    internal sealed class JobReadGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitAsync(CancellationToken cancellationToken) => _read.Task.WaitAsync(cancellationToken);

        public void Release() => _release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default
        )
        {
            var sql = command.CommandText;
            if (
                sql.Contains("JOIN", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("event_source", StringComparison.Ordinal)
                && sql.Contains("event_subscription", StringComparison.Ordinal)
            )
            {
                _read.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
