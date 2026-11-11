using Agw.Events.Domain.Behaviors;
using Agw.Events.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Events;
using Xunit;

namespace Agw.Events.Tests;

public sealed class EventScheduleBehaviorTests
{
    [Fact]
    public void Upsert_ScheduleBySequence_DistinguishesDuplicateStaleAndNew()
    {
        var schedule = new EventSchedule
        {
            RequestSeq = 4,
            Revision = 2,
            Occurrence = 6,
            State = EventScheduleState.Active,
            Kind = EventScheduleKind.Once,
            DueAt = new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero),
            TimeZone = "Etc/UTC",
            EventType = "com.example.old",
        };
        var behavior = new EventScheduleBehavior(schedule);

        var duplicateAccepted = behavior.Upsert(4, Definition("com.example.duplicate"));
        var duplicateType = schedule.EventType;
        var staleAccepted = behavior.Upsert(3, Definition("com.example.stale"));
        var staleType = schedule.EventType;
        var newAccepted = behavior.Upsert(5, Definition("com.example.new"));

        Assert.True(duplicateAccepted);
        Assert.Equal("com.example.old", duplicateType);
        Assert.False(staleAccepted);
        Assert.Equal("com.example.old", staleType);
        Assert.True(newAccepted);
        Assert.Equal(5, schedule.RequestSeq);
        Assert.Equal(3, schedule.Revision);
        Assert.Equal(0, schedule.Occurrence);
        Assert.Equal(EventScheduleState.Active, schedule.State);
        Assert.Equal("com.example.new", schedule.EventType);
        Assert.True(schedule.DataIsBinary);
    }

    [Fact]
    public void Cancel_BeforeUpsert_CreatesVersionedTombstone()
    {
        var schedule = new EventSchedule
        {
            CreateBy = "owner",
            CallerKey = "ext:caller",
            BusinessKey = "matter:42",
        };
        var behavior = new EventScheduleBehavior(schedule);

        var canceled = behavior.Cancel(8);
        var staleUpsertAccepted = behavior.Upsert(7, Definition("com.example.late"));

        Assert.True(canceled);
        Assert.False(staleUpsertAccepted);
        Assert.Equal(EventScheduleState.Cancelled, schedule.State);
        Assert.Equal(8, schedule.RequestSeq);
        Assert.Equal(1, schedule.Revision);
        Assert.Null(schedule.Kind);
        Assert.Null(schedule.DueAt);
        Assert.Null(schedule.EventType);
    }

    [Fact]
    public void Fire_WithoutNextOccurrence_CompletesSchedule()
    {
        var plannedAt = new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);
        var schedule = new EventSchedule
        {
            DueAt = plannedAt,
            State = EventScheduleState.Active,
            Occurrence = 2,
            Version = 4,
        };

        new EventScheduleBehavior(schedule).Fire(plannedAt, null);

        Assert.Null(schedule.DueAt);
        Assert.Equal(EventScheduleState.Completed, schedule.State);
        Assert.Equal(3, schedule.Occurrence);
        Assert.Equal(plannedAt, schedule.LastFiredAt);
        Assert.Equal(5, schedule.Version);
    }

    private static EventScheduleDefinition Definition(string eventType) =>
        new()
        {
            Kind = EventScheduleKind.Interval,
            DueAt = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero),
            Interval = TimeSpan.FromMinutes(15),
            TimeZone = "Etc/UTC",
            EventType = eventType,
            DataIsBinary = true,
        };
}
