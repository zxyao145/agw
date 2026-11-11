using Agw.Events.Application;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Infrastructure.Events;

public sealed partial class EventStagingTests
{
    [Fact]
    public async Task RunAsync_OnceSchedule_PreservesTemplateAndCompletesOnce()
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(true);
        var planned = TimeProvider.System.GetUtcNow().AddMinutes(-1);
        Guid scheduleId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            var schedule = new EventSchedule
            {
                Id = Guid.CreateVersion7(),
                CreateBy = "owner",
                CallerKey = "ext:019a4c17-0000-7000-8000-000000000001",
                BusinessKey = "once",
                Kind = EventScheduleKind.Once,
                State = EventScheduleState.Active,
                DueAt = planned,
                TimeZone = "UTC",
                EventType = "example.due",
                DataContentType = "application/octet-stream",
                Data = Convert.ToBase64String([1, 2, 3]),
                DataIsBinary = true,
                DataSchema = "https://example.test/schema",
                Summary = "Scheduled notice",
                Link = "https://example.test/item",
                RequestSeq = 1,
                Revision = 1,
            };
            scheduleId = schedule.Id;
            db.EventSchedules.Add(schedule);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using (var scope = host.Services.CreateAsyncScope())
            Assert.True(
                await scope
                    .ServiceProvider.GetRequiredService<EventScheduleRunner>()
                    .RunAsync(scheduleId, TestContext.Current.CancellationToken)
            );
        await using (var scope = host.Services.CreateAsyncScope())
        {
            Assert.False(
                await scope
                    .ServiceProvider.GetRequiredService<EventScheduleRunner>()
                    .RunAsync(scheduleId, TestContext.Current.CancellationToken)
            );
            var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            var record = await db.Events.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal($"{scheduleId}:1:1", record.CeId);
            Assert.Equal(planned, record.CeTime);
            Assert.Equal(record.Id, record.ChainId);
            Assert.Equal("AQID", record.CeData);
            Assert.True(record.CeDataIsBinary);
            Assert.Equal("https://example.test/schema", record.CeDataSchema);
            Assert.Equal("Scheduled notice", record.Summary);
            Assert.Equal(
                EventScheduleState.Completed,
                (await db.EventSchedules.SingleAsync(TestContext.Current.CancellationToken)).State
            );
            Assert.Single(await db.EventOutbox.ToArrayAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task RunAsync_MissedInterval_FiresOnceAndAdvancesFromCurrentTime()
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(true);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var now = TimeProvider.System.GetUtcNow();
        var schedule = new EventSchedule
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            CallerKey = "ext:019a4c17-0000-7000-8000-000000000001",
            BusinessKey = "interval",
            Kind = EventScheduleKind.Interval,
            State = EventScheduleState.Active,
            DueAt = now.AddDays(-3),
            Interval = TimeSpan.FromHours(1),
            TimeZone = "UTC",
            EventType = "example.due",
            RequestSeq = 1,
            Revision = 1,
        };
        db.EventSchedules.Add(schedule);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True(
            await scope
                .ServiceProvider.GetRequiredService<EventScheduleRunner>()
                .RunAsync(schedule.Id, TestContext.Current.CancellationToken)
        );
        Assert.True(schedule.DueAt >= now.AddHours(1));
        Assert.Equal(1, schedule.Occurrence);
        Assert.Single(await db.Events.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_CronInNonUtcTimeZone_AdvancesToLocalOccurrence()
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(true);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var now = TimeProvider.System.GetUtcNow();
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Singapore");
        var schedule = new EventSchedule
        {
            Id = Guid.CreateVersion7(),
            CreateBy = "owner",
            CallerKey = "ext:019a4c17-0000-7000-8000-000000000001",
            BusinessKey = "cron",
            Kind = EventScheduleKind.Cron,
            State = EventScheduleState.Active,
            DueAt = now.AddMinutes(-1),
            Cron = "0 9 * * *",
            TimeZone = timeZone.Id,
            EventType = "example.daily",
            RequestSeq = 1,
            Revision = 1,
        };
        db.EventSchedules.Add(schedule);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(
            await scope
                .ServiceProvider.GetRequiredService<EventScheduleRunner>()
                .RunAsync(schedule.Id, TestContext.Current.CancellationToken)
        );

        var dueAt = await db
            .EventSchedules.Where(row => row.Id == schedule.Id)
            .Select(row => row.DueAt)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(dueAt);
        var localDueAt = TimeZoneInfo.ConvertTime(dueAt.Value, timeZone);
        Assert.Equal(9, localDueAt.Hour);
        Assert.Equal(0, localDueAt.Minute);
    }
}
