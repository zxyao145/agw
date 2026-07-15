using Agw.Jobs.Scheduling;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;

namespace Agw.Jobs.Tests;

public class JobScheduleCalculatorTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(TriggerType.Once, "2026-07-15T09:00:00Z", "2026-07-15T09:00:00+00:00")]
    [InlineData(TriggerType.Interval, "00:15:00", "2026-07-15T08:15:00+00:00")]
    [InlineData(TriggerType.Cron, "15 8 * * *", "2026-07-15T08:15:00+00:00")]
    public void GetNextRunTime_ValidTrigger_ReturnsExpected(
        TriggerType triggerType,
        string triggerValue,
        string expected)
    {
        var calculator = new JobScheduleCalculator();
        var job = new Job { TriggerType = triggerType, TriggerValue = triggerValue };

        var result = calculator.GetNextRunTime(job, UtcNow);

        Assert.Equal(DateTimeOffset.Parse(expected), result);
    }

    [Fact]
    public void GetNextRunTime_PastOnceTrigger_ReturnsNull()
    {
        var calculator = new JobScheduleCalculator();
        var job = new Job
        {
            TriggerType = TriggerType.Once,
            TriggerValue = "2026-07-15T07:00:00Z"
        };

        var result = calculator.GetNextRunTime(job, UtcNow);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(TriggerType.Once, "not-a-date", 400_0032)]
    [InlineData(TriggerType.Interval, "00:00:00", 400_0033)]
    public void GetNextRunTime_InvalidTrigger_ThrowsExpectedAgwException(
        TriggerType triggerType,
        string triggerValue,
        int expectedCode)
    {
        var calculator = new JobScheduleCalculator();
        var job = new Job { TriggerType = triggerType, TriggerValue = triggerValue };

        var exception = Assert.Throws<AgwException>(() => calculator.GetNextRunTime(job, UtcNow));

        Assert.Equal(expectedCode, exception.Code);
    }
}
