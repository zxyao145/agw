using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Scheduling.Attempts;

public sealed record JobAttemptClaim(Job Job, Guid ExecutionId, DateTimeOffset StartedAt, int Attempt);
