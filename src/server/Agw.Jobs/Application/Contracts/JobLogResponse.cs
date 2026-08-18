namespace Agw.Jobs.Application.Contracts;

public record JobLogResponse(
    Guid Id,
    Guid JobId,
    string? ContextId,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    bool Success,
    int Attempt,
    string? ErrorMessage
);
