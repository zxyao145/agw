namespace Agw.Extensions.TaskPilot;

public sealed record TaskPilotRuleHandlerConfig
{
    public required Guid RuleId { get; init; }
}

public sealed record TaskPilotReviewDueData
{
    public required Guid MatterId { get; init; }

    public required Guid ReviewId { get; init; }

    public required long RequestSeq { get; init; }
}

public sealed record TaskPilotRetryDueData
{
    public required Guid MatterId { get; init; }

    public Guid? RoundId { get; init; }

    public required long RequestSeq { get; init; }

    public bool WakeOnly { get; init; }
}

public sealed record TaskPilotCancelReviewScheduleRequest
{
    public required string BusinessKey { get; init; }

    public required long RequestSeq { get; init; }
}
