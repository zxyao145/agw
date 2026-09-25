namespace Agw.Jobs.Contracts;

public record JobLogResponse(
    Guid Id,
    Guid JobId,
    Guid? ConversationId,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    bool Success,
    int Attempt,
    string? ErrorMessage
);
