using Agw.Jobs.Contracts;

namespace Agw.Jobs.Entities;

internal sealed class ManagedHangfireJobDefinition
{
    public Guid Id { get; init; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public HangfireManagedJobType JobType { get; set; }
    public HangfireManagedJobStatus Status { get; set; }
    public string Queue { get; set; } = "default";
    public string? CronExpression { get; set; }
    public int? DelaySeconds { get; set; }
    public string? Payload { get; set; }
    public string? RecurringJobId { get; set; }
    public string? BackgroundJobId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public string? CreatedBy { get; init; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset? LastExecutionUtc { get; set; }
    public DateTimeOffset? NextExecutionUtc { get; set; }
    public string? LastError { get; set; }
}
