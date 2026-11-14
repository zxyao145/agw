using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Contracts.Execution;
using Agw.Auth.Contracts;
using Agw.Events.Application;
using Agw.Events.Contracts;
using Agw.Events.Contracts.Transport;
using Agw.Events.Infrastructure.Delivery;
using Agw.Events.Infrastructure.Dispatch;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Testing;
using Agw.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Agw.Events.Tests;

public sealed class EventJobFacadeTests
{
    private const string Owner = "owner";
    private const string FiredType = "job.timer.fired";
    private static readonly Guid DefaultProjectId = Guid.CreateVersion7();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateAsync_CronJob_SavesTimerSubscriptionAndActiveSchedule()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(Definition(), Ct);
        var source = await fixture.SourceAsync(job.Id);
        var subscription = await fixture.SubscriptionAsync(job.Id);
        var schedule = await fixture.ScheduleAsync(source.Id);
        using var config = JsonDocument.Parse(source.ConfigJson);
        var template = config.RootElement.GetProperty("event");

        Assert.Equal(EventSourceKind.Timer, source.Kind);
        Assert.Equal(EventManagedBy.Job, source.ManagedBy);
        Assert.Equal(job.Id.ToString("D"), source.ManagedKey);
        Assert.Equal(EventManagedBy.Job, subscription.ManagedBy);
        Assert.Equal(job.Id.ToString("D"), subscription.ManagedKey);
        Assert.Equal($"[\"{source.Id}\"]", subscription.SourceIdsJson);
        Assert.Equal("[\"job.timer.fired\"]", subscription.EventTypesJson);
        Assert.Null(subscription.FilterJson);
        Assert.Equal((int)AgentExecutionPermissionMode.FullAccess, subscription.PermissionMode);
        Assert.Equal("UTC", config.RootElement.GetProperty("timeZone").GetString());
        Assert.Equal(FiredType, template.GetProperty("type").GetString());
        Assert.Equal(job.Id.ToString("D"), template.GetProperty("subject").GetString());
        Assert.Equal($"source:{source.Id}", schedule.CallerKey);
        Assert.Equal(schedule.CallerKey, schedule.BusinessKey);
        Assert.Equal(EventScheduleState.Active, schedule.State);
        Assert.Equal(schedule.DueAt, job.DueAt);
        Assert.Null(job.LastDelivery);
    }

    [Fact]
    public async Task CreateAsync_DisabledJob_SavesNoSchedule()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(Definition() with { Enabled = false }, Ct);

        Assert.Null(job.DueAt);
        Assert.Empty(await fixture.Db.EventSchedules.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("Interval", "PT0S", null)]
    [InlineData("Interval", "P2147483647D", null)]
    [InlineData("Interval", "P10675198D", null)]
    [InlineData("Cron", null, "not a cron")]
    [InlineData("Cron", null, "0 0 1 * * *")]
    [InlineData("Once", null, null)]
    [InlineData("Weekly", null, null)]
    public async Task CreateAsync_InvalidSchedule_ThrowsInvalidParamAndSavesNothing(
        string kind,
        string? interval,
        string? cron
    )
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var definition = Definition() with
        {
            Schedule = new EventJobSchedule
            {
                Kind = kind,
                Interval = interval,
                Cron = cron,
            },
        };

        var exception = await Assert.ThrowsAsync<AgwException>(() => fixture.Jobs.CreateAsync(definition, Ct));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Empty(await fixture.Db.EventSources.ToListAsync(Ct));
        Assert.Empty(await fixture.Db.EventSubscriptions.ToListAsync(Ct));
        Assert.Empty(await fixture.Db.EventSchedules.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(201, "run")]
    [InlineData(0, "run")]
    [InlineData(10, " ")]
    public async Task CreateAsync_InvalidNameOrInstructions_ThrowsInvalidParam(int nameLength, string instructions)
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var definition = Definition() with { Name = new string('a', nameLength), Instructions = instructions };

        var exception = await Assert.ThrowsAsync<AgwException>(() => fixture.Jobs.CreateAsync(definition, Ct));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task CreateAsync_NameAtLimit_CreatesJob()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var name = new string('a', 200);

        var job = await fixture.Jobs.CreateAsync(Definition() with { Name = name }, Ct);

        Assert.Equal(name, job.Definition.Name);
    }

    [Fact]
    public async Task CreateAsync_SubscriptionInsertRejected_SavesNothing()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER reject_job_subscription BEFORE INSERT ON event_subscription BEGIN SELECT RAISE(ABORT, 'rejected'); END;",
            Ct
        );

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Jobs.CreateAsync(Definition(), Ct));

        await using var readScope = fixture.Services.CreateAsyncScope();
        var db = readScope.ServiceProvider.GetRequiredService<AgwDbContext>();
        Assert.Empty(await db.EventSources.ToListAsync(Ct));
        Assert.Empty(await db.EventSubscriptions.ToListAsync(Ct));
        Assert.Empty(await db.EventSchedules.ToListAsync(Ct));
    }

    [Fact]
    public async Task CreateAsync_IntervalWithDays_SchedulesAfterOneDay()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var definition = Definition() with
        {
            Schedule = new EventJobSchedule { Kind = "Interval", Interval = "P1D" },
        };
        var before = TimeProvider.System.GetUtcNow();

        var job = await fixture.Jobs.CreateAsync(definition, Ct);

        Assert.InRange(job.DueAt!.Value - before, TimeSpan.FromDays(1), TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task UpdateAsync_NameProjectTargetInstructions_KeepsScheduleDueAtAndRevision()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(Definition(), Ct);
        var before = await fixture.JobScheduleAsync(job.Id);
        var definition = job.Definition with
        {
            Name = "renamed",
            ProjectId = Guid.CreateVersion7(),
            TargetKind = EventJobTargetKind.Agentflow,
            AgentId = Guid.CreateVersion7(),
            Instructions = "updated instructions",
        };
        await fixture.SeedProjectAsync(definition.ProjectId);

        var updated = await fixture.Jobs.UpdateAsync(job.Id, definition, Ct);

        var after = await fixture.JobScheduleAsync(job.Id);
        Assert.Equal(before.DueAt, after.DueAt);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(definition, updated.Definition);
        Assert.Equal(definition.Name, (await fixture.SourceAsync(job.Id)).Name);
        Assert.Equal(definition.Name, (await fixture.SubscriptionAsync(job.Id)).Name);
    }

    [Fact]
    public async Task UpdateAsync_ScheduleChanged_IncrementsScheduleRevision()
    {
        using var user = PushUser();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 14, 0, 15, 0, TimeSpan.Zero));
        await using var fixture = await Fixture.CreateAsync(clock);
        var job = await fixture.Jobs.CreateAsync(Definition(), Ct);
        var before = await fixture.JobScheduleAsync(job.Id);
        var definition = job.Definition with { Schedule = job.Definition.Schedule with { Cron = "0 2 * * *" } };

        var updated = await fixture.Jobs.UpdateAsync(job.Id, definition, Ct);

        var after = await fixture.JobScheduleAsync(job.Id);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 1, 0, 0, TimeSpan.Zero), before.DueAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 2, 0, 0, TimeSpan.Zero), after.DueAt);
        Assert.Equal(after.DueAt, updated.DueAt);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal("0 2 * * *", updated.Definition.Schedule.Cron);
    }

    [Fact]
    public async Task SetEnabledAsync_Disable_CancelledTimerDoesNotPublishAtOldDueAt()
    {
        using var user = PushUser();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));
        await using var fixture = await Fixture.CreateAsync(clock);
        var job = await fixture.Jobs.CreateAsync(
            Definition() with
            {
                Schedule = new EventJobSchedule { Kind = "Once", At = clock.GetUtcNow().AddHours(1) },
            },
            Ct
        );
        var schedule = await fixture.JobScheduleAsync(job.Id);

        await fixture.Jobs.SetEnabledAsync(job.Id, false, Ct);
        clock.SetUtcNow(schedule.DueAt!.Value);

        Assert.False(await fixture.RunAsync(schedule.Id));
        Assert.Equal(EventScheduleState.Cancelled, (await fixture.JobScheduleAsync(job.Id)).State);
        Assert.Empty(await fixture.Db.Events.Where(row => row.Type == FiredType).ToListAsync(Ct));
    }

    [Fact]
    public async Task SetEnabledAsync_DisableThenEnable_CancelsAndRestoresSchedule()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(Definition(), Ct);

        var disabled = await fixture.Jobs.SetEnabledAsync(job.Id, false, Ct);
        Assert.Equal(EventScheduleState.Cancelled, (await fixture.JobScheduleAsync(job.Id)).State);
        Assert.False((await fixture.SourceAsync(job.Id)).Enabled);
        Assert.False((await fixture.SubscriptionAsync(job.Id)).Enabled);
        Assert.Null(disabled.DueAt);

        var enabled = await fixture.Jobs.SetEnabledAsync(job.Id, true, Ct);
        Assert.Equal(EventScheduleState.Active, (await fixture.JobScheduleAsync(job.Id)).State);
        Assert.NotNull(enabled.DueAt);
    }

    [Fact]
    public async Task CreateAsync_OnceInPast_FiresOneEventAndOneDelivery()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(OnceInPast(), Ct);
        var schedule = await fixture.JobScheduleAsync(job.Id);

        Assert.True(await fixture.RunAsync(schedule.Id));
        Assert.False(await fixture.RunAsync(schedule.Id));
        await fixture.DispatchAsync();

        Assert.Equal(EventScheduleState.Completed, (await fixture.JobScheduleAsync(job.Id)).State);
        Assert.Null((await fixture.Jobs.FindAsync(job.Id, Ct))!.DueAt);
        var record = await fixture.Db.Events.SingleAsync(row => row.Type == FiredType, Ct);
        Assert.Equal($"{schedule.Id}:{schedule.Revision}:1", record.CeId);
        Assert.Equal(job.Id.ToString("D"), record.Subject);
        var subscription = await fixture.SubscriptionAsync(job.Id);
        Assert.Single(
            await fixture.Db.EventDeliveries.Where(row => row.SubscriptionId == subscription.Id).ToListAsync(Ct)
        );
    }

    [Fact]
    public async Task UpdateAsync_CompletedOnceRenamed_DoesNotFireAgain()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(OnceInPast(), Ct);
        var schedule = await fixture.JobScheduleAsync(job.Id);
        Assert.True(await fixture.RunAsync(schedule.Id));

        await fixture.Jobs.UpdateAsync(job.Id, job.Definition with { Name = "renamed" }, Ct);

        Assert.False(await fixture.RunAsync(schedule.Id));
        Assert.Equal(1, await fixture.Db.Events.CountAsync(row => row.Type == FiredType, Ct));
    }

    [Fact]
    public async Task SetEnabledAsync_ReenableCompletedOnce_FiresAgain()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(OnceInPast(), Ct);
        var schedule = await fixture.JobScheduleAsync(job.Id);
        Assert.True(await fixture.RunAsync(schedule.Id));

        await fixture.Jobs.SetEnabledAsync(job.Id, false, Ct);
        await fixture.Jobs.SetEnabledAsync(job.Id, true, Ct);

        Assert.True(await fixture.RunAsync(schedule.Id));
        Assert.Equal(2, await fixture.Db.Events.CountAsync(row => row.Type == FiredType, Ct));
    }

    [Fact]
    public async Task RunAsync_PreviousDeliveryUnfinished_CreatesNewDelivery()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var definition = Definition() with
        {
            Schedule = new EventJobSchedule { Kind = "Interval", Interval = "PT1M" },
        };
        var job = await fixture.Jobs.CreateAsync(definition, Ct);
        var schedule = await fixture.JobScheduleAsync(job.Id);

        for (var occurrence = 0; occurrence < 2; occurrence++)
        {
            await fixture
                .Db.EventSchedules.Where(row => row.Id == schedule.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(row => row.DueAt, TimeProvider.System.GetUtcNow().AddSeconds(-1)),
                    Ct
                );
            Assert.True(await fixture.RunAsync(schedule.Id));
            await fixture.DispatchAsync();
        }

        var subscription = await fixture.SubscriptionAsync(job.Id);
        Assert.Equal(2, await fixture.Db.EventDeliveries.CountAsync(row => row.SubscriptionId == subscription.Id, Ct));
    }

    [Fact]
    public async Task DeleteAsync_ExistingJob_MarksTimerDeletedRemovesSubscriptionAndCancelsSchedule()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(Definition(), Ct);

        var deleted = await fixture.Jobs.DeleteAsync(job.Id, Ct);

        Assert.Equal(job, deleted);
        var source = await fixture.SourceAsync(job.Id);
        Assert.NotNull(source.DeletedAt);
        Assert.False(source.Enabled);
        var jobKey = job.Id.ToString("D");
        Assert.False(
            await fixture.Db.EventSubscriptions.AnyAsync(
                row => row.ManagedBy == EventManagedBy.Job && row.ManagedKey == jobKey,
                Ct
            )
        );
        Assert.Equal(EventScheduleState.Cancelled, (await fixture.JobScheduleAsync(job.Id)).State);
        Assert.Null(await fixture.Jobs.FindAsync(job.Id, Ct));
    }

    [Fact]
    public async Task DeleteAsync_JobReference_MakesAgentDeletable()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "job-agent",
            DisplayName = "Job Agent",
        };
        fixture.Db.Agents.Add(agent);
        await fixture.Db.SaveChangesAsync(Ct);
        var job = await fixture.Jobs.CreateAsync(Definition() with { AgentId = agent.Id }, Ct);
        var deletion = new AgentDeletionCoordinator(
            fixture.Db,
            fixture.Services.GetRequiredService<IApplicationLock>()
        );

        var inUse = await Assert.ThrowsAsync<AgwException>(() => deletion.DeleteAsync(agent.Id, Owner, Ct));

        Assert.Equal(ErrorCodes.AgentInUse.Code, inUse.Code);
        await fixture.Jobs.DeleteAsync(job.Id, Ct);
        fixture.Db.ChangeTracker.Clear();

        Assert.True(await deletion.DeleteAsync(agent.Id, Owner, Ct));
        Assert.False(await fixture.Db.Agents.AnyAsync(row => row.Id == agent.Id, Ct));
    }

    [Fact]
    public async Task DeleteAsync_PendingTimerEvent_DispatchesAsNoMatch()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(OnceInPast(), Ct);
        Assert.True(await fixture.RunAsync((await fixture.JobScheduleAsync(job.Id)).Id));

        await fixture.Jobs.DeleteAsync(job.Id, Ct);
        await fixture.DispatchAsync();

        var record = await fixture.Db.Events.SingleAsync(row => row.Type == FiredType, Ct);
        Assert.Equal(EventDispatchState.NoMatch, record.DispatchState);
        Assert.Empty(await fixture.Db.EventDeliveries.ToListAsync(Ct));
    }

    [Fact]
    public async Task DeleteAsync_UnclaimedDelivery_IsCancelledWhenProcessed()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(OnceInPast(), Ct);
        Assert.True(await fixture.RunAsync((await fixture.JobScheduleAsync(job.Id)).Id));
        await fixture.DispatchAsync();

        await fixture.Jobs.DeleteAsync(job.Id, Ct);
        await fixture.ProcessDeliveryAsync();

        Assert.Equal(EventDeliveryState.Cancelled, (await fixture.Db.EventDeliveries.SingleAsync(Ct)).State);
    }

    [Fact]
    public async Task FindAsync_OtherUsersJob_ReturnsNull()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(Definition(), Ct);

        using var other = PushUser("other");
        Assert.Null(await fixture.Jobs.FindAsync(job.Id, Ct));
    }

    [Theory]
    [InlineData("update")]
    [InlineData("enable")]
    [InlineData("delete")]
    [InlineData("runs")]
    public async Task Operation_OtherUsersJob_ThrowsJobNotFound(string operation)
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(Definition(), Ct);

        using var other = PushUser("other");
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            operation switch
            {
                "update" => fixture.Jobs.UpdateAsync(job.Id, job.Definition, Ct),
                "enable" => fixture.Jobs.SetEnabledAsync(job.Id, false, Ct),
                "delete" => fixture.Jobs.DeleteAsync(job.Id, Ct),
                "runs" => fixture.Jobs.ListRunsAsync(job.Id, 0, 10, Ct),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            }
        );

        Assert.Equal(ErrorCodes.JobNotFound.Code, exception.Code);
    }

    [Fact]
    public async Task ListAsync_ProjectFilterAndDeliveries_ReturnsLatestDeliveryOrderedByDueAt()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var definition = OnceInPast();
        var first = await fixture.Jobs.CreateAsync(definition, Ct);
        var disabled = await fixture.Jobs.CreateAsync(definition with { Enabled = false }, Ct);
        var anotherProjectDefinition = Definition() with { ProjectId = Guid.CreateVersion7() };
        await fixture.SeedProjectAsync(anotherProjectDefinition.ProjectId);
        var anotherProject = await fixture.Jobs.CreateAsync(anotherProjectDefinition, Ct);
        var subscription = await fixture.SubscriptionAsync(first.Id);
        var previous = Delivery(subscription.Id, EventDeliveryState.Succeeded);
        var latest = Delivery(subscription.Id, EventDeliveryState.RetryScheduled);
        latest.Id = Guid.Parse("ffffffff-ffff-7fff-8fff-ffffffffffff");
        fixture.Db.EventDeliveries.AddRange(previous, latest);
        await fixture.Db.SaveChangesAsync(Ct);

        var jobs = await fixture.Jobs.ListAsync(null, Ct);
        var filtered = await fixture.Jobs.ListAsync(definition.ProjectId, Ct);

        Assert.Equal([first.Id, anotherProject.Id, disabled.Id], jobs.Select(job => job.Id));
        Assert.Equal([first.Id, disabled.Id], filtered.Select(job => job.Id));
        Assert.Equal(latest.Id, jobs[0].LastDelivery!.Id);
        Assert.Null(jobs[^1].DueAt);
    }

    [Fact]
    public async Task FindAsync_TargetMissing_ReturnsLastDeliveryError()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Jobs.CreateAsync(OnceInPast(), Ct);
        Assert.True(await fixture.RunAsync((await fixture.JobScheduleAsync(created.Id)).Id));
        await fixture.DispatchAsync();
        await fixture.ProcessDeliveryAsync();

        var job = await fixture.Jobs.FindAsync(created.Id, Ct);

        Assert.Equal((int)EventDeliveryState.RetryScheduled, job!.LastDelivery!.State);
        Assert.Equal(1, job.LastDelivery.Attempt);
        Assert.False(string.IsNullOrWhiteSpace(job.LastDelivery.LastError));
    }

    [Fact]
    public async Task ListRunsAsync_FinishedAndRunningAttempts_ReturnsFinishedNewestFirst()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(Definition(), Ct);
        var subscription = await fixture.SubscriptionAsync(job.Id);
        var finished = Delivery(subscription.Id, EventDeliveryState.Succeeded);
        var waiting = Delivery(subscription.Id, EventDeliveryState.WaitingExecution);
        waiting.Attempt = 2;
        var now = TimeProvider.System.GetUtcNow();
        var first = Attempt(finished.Id, 1, now.AddMinutes(-3));
        var second = Attempt(waiting.Id, 1, now.AddMinutes(-2));
        var current = Attempt(waiting.Id, 2, now.AddMinutes(-1));
        fixture.Db.EventDeliveries.AddRange(finished, waiting);
        fixture.Db.EventDeliveryAttempts.AddRange(first, second, current);
        await fixture.Db.SaveChangesAsync(Ct);

        var runs = await fixture.Jobs.ListRunsAsync(job.Id, 0, 10, Ct);
        var secondPage = await fixture.Jobs.ListRunsAsync(job.Id, 1, 1, Ct);

        Assert.Equal([second.Id, first.Id], runs.Select(run => run.Id));
        Assert.Equal(first.TurnId, Assert.Single(secondPage).TurnId);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, 501)]
    public async Task ListRunsAsync_OffsetOrLimitOutOfRange_ThrowsInvalidParam(int offset, int limit)
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Jobs.CreateAsync(Definition(), Ct);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Jobs.ListRunsAsync(job.Id, offset, limit, Ct)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task CountAsync_DeletedAndOtherUsersJobs_AreNotCounted()
    {
        using var user = PushUser();
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Jobs.CreateAsync(Definition(), Ct);
        var deleted = await fixture.Jobs.CreateAsync(Definition(), Ct);
        await fixture.Jobs.DeleteAsync(deleted.Id, Ct);
        using (PushUser("other"))
        {
            var otherDefinition = Definition() with { ProjectId = Guid.CreateVersion7() };
            await fixture.SeedProjectAsync(otherDefinition.ProjectId);
            await fixture.Jobs.CreateAsync(otherDefinition, Ct);
        }

        Assert.Equal(1, await fixture.Jobs.CountAsync(Ct));
    }

    private static EventJobDefinition Definition() =>
        new()
        {
            Name = "Job",
            Enabled = true,
            ProjectId = DefaultProjectId,
            TargetKind = EventJobTargetKind.Agent,
            AgentId = Guid.CreateVersion7(),
            Instructions = "Run",
            Schedule = new EventJobSchedule { Kind = "Cron", Cron = "0 1 * * *" },
        };

    private static EventJobDefinition OnceInPast() =>
        Definition() with
        {
            Schedule = new EventJobSchedule { Kind = "Once", At = TimeProvider.System.GetUtcNow().AddMinutes(-1) },
        };

    private static EventDelivery Delivery(Guid subscriptionId, EventDeliveryState state) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            CreateBy = Owner,
            EventId = Guid.CreateVersion7(),
            SubscriptionId = subscriptionId,
            State = state,
            Attempt = 1,
            RecoverAfter = TimeProvider.System.GetUtcNow(),
        };

    private static EventDeliveryAttempt Attempt(Guid deliveryId, int attempt, DateTimeOffset startedAt) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            DeliveryId = deliveryId,
            Attempt = attempt,
            TurnId = Guid.CreateVersion7(),
            StartedAt = startedAt,
            EndedAt = startedAt.AddSeconds(1),
            Result = EventDeliveryAttemptResult.Succeeded,
        };

    private static IDisposable PushUser(string userId = Owner) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
        );

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly IAsyncDisposable _host;
        private readonly IServiceProvider _services;
        private readonly AsyncServiceScope _scope;

        private Fixture(ModuleCompositionTestHost host)
            : this(host.Services, host) { }

        private Fixture(IServiceProvider services, IAsyncDisposable host)
        {
            _host = host;
            _services = services;
            _scope = services.CreateAsyncScope();
        }

        public IServiceProvider Services => _services;
        public AgwDbContext Db => _scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        public IEventJobFacade Jobs => _scope.ServiceProvider.GetRequiredService<IEventJobFacade>();

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(await ModuleCompositionTestHost.CreateAsync());
            await fixture.SeedProjectAsync(DefaultProjectId);
            return fixture;
        }

        public static async Task<Fixture> CreateAsync(TestTimeProvider timeProvider)
        {
            var root = Path.Combine(AppContext.BaseDirectory, $"event-job-composition-{Guid.CreateVersion7():N}");
            var descriptors = new ServiceCollection();
            ModuleCompositionTestHost.ConfigureServices(descriptors, root);
            descriptors.RemoveAll<TimeProvider>();
            descriptors.AddSingleton<TimeProvider>(timeProvider);
            var services = descriptors.BuildServiceProvider();
            await ModuleCompositionTestHost.InitializeAsync(services);
            var fixture = new Fixture(services, new OwnedTestHost(services, root));
            await fixture.SeedProjectAsync(DefaultProjectId);
            return fixture;
        }

        public async Task SeedProjectAsync(Guid projectId)
        {
            if (await Db.Projects.AnyAsync(row => row.Id == projectId, Ct))
            {
                return;
            }

            Db.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = $"Job project {projectId:D}",
                    Workspace = Services.GetRequiredService<AgwDataPaths>().UserHomeDirectory,
                }
            );
            await Db.SaveChangesAsync(Ct);
        }

        public Task<EventSource> SourceAsync(Guid jobId)
        {
            var jobKey = jobId.ToString("D");
            return Db
                .EventSources.AsNoTracking()
                .SingleAsync(row => row.ManagedBy == EventManagedBy.Job && row.ManagedKey == jobKey, Ct);
        }

        public Task<EventSubscription> SubscriptionAsync(Guid jobId)
        {
            var jobKey = jobId.ToString("D");
            return Db
                .EventSubscriptions.AsNoTracking()
                .SingleAsync(row => row.ManagedBy == EventManagedBy.Job && row.ManagedKey == jobKey, Ct);
        }

        public Task<EventSchedule> ScheduleAsync(Guid sourceId) =>
            Db.EventSchedules.AsNoTracking().SingleAsync(row => row.CallerKey == $"source:{sourceId}", Ct);

        public async Task<EventSchedule> JobScheduleAsync(Guid jobId) =>
            await ScheduleAsync((await SourceAsync(jobId)).Id);

        public async Task<bool> RunAsync(Guid scheduleId)
        {
            await using var scope = Services.CreateAsyncScope();
            var ran = await scope.ServiceProvider.GetRequiredService<EventScheduleRunner>().RunAsync(scheduleId, Ct);
            Db.ChangeTracker.Clear();
            return ran;
        }

        public async Task DispatchAsync()
        {
            await using var scope = Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var message = await ModuleCompositionTestHost.ReadTransportDeliveryAsync(
                services.GetRequiredService<IEventTransport>(),
                EventTopics.Accepted
            );
            await services.GetRequiredService<EventDispatcher>().DispatchAsync(message, Ct);
        }

        public async Task ProcessDeliveryAsync()
        {
            await using var scope = Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var message = await ModuleCompositionTestHost.ReadTransportDeliveryAsync(
                services.GetRequiredService<IEventTransport>(),
                EventTopics.Delivery
            );
            await services.GetRequiredService<EventDeliveryProcessor>().ProcessAsync(message, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _host.DisposeAsync();
        }

        private sealed class OwnedTestHost(ServiceProvider services, string root) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await services.DisposeAsync();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }
}
