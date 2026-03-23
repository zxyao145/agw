namespace Agw.Jobs.Contracts;

public enum HangfireManagedJobType
{
    FireAndForget = 0,
    Delayed = 1,
    Recurring = 2
}

public enum HangfireManagedJobStatus
{
    Pending = 0,
    Scheduled = 1,
    Enqueued = 2,
    Processing = 3,
    Succeeded = 4,
    Failed = 5,
    Paused = 6,
    Deleted = 7
}

public sealed record HangfireJobUpsertRequest(
    string Name,
    string? Description,
    HangfireManagedJobType JobType,
    string? CronExpression,
    int? DelaySeconds,
    string? Queue,
    string? Payload);

public sealed record HangfireJobSummaryResponse(
    Guid Id,
    string Name,
    string? Description,
    HangfireManagedJobType JobType,
    HangfireManagedJobStatus Status,
    string Queue,
    string? CronExpression,
    int? DelaySeconds,
    string? Payload,
    string? RecurringJobId,
    string? BackgroundJobId,
    string? HangfireState,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? LastExecutionUtc,
    DateTimeOffset? NextExecutionUtc,
    string? LastError);

public sealed record HangfireJobDetailResponse(
    Guid Id,
    string Name,
    string? Description,
    HangfireManagedJobType JobType,
    HangfireManagedJobStatus Status,
    string Queue,
    string? CronExpression,
    int? DelaySeconds,
    string? Payload,
    string? RecurringJobId,
    string? BackgroundJobId,
    string? HangfireState,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? LastExecutionUtc,
    DateTimeOffset? NextExecutionUtc,
    string? LastError,
    IReadOnlyList<HangfireJobStateHistoryResponse> History);

public sealed record HangfireJobStateHistoryResponse(
    string StateName,
    DateTimeOffset? CreatedAtUtc,
    string? Reason,
    IReadOnlyDictionary<string, string> Data);
