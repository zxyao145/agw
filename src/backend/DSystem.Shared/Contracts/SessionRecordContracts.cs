using DSystem.Shared.Models;

namespace DSystem.Shared.Contracts;

public record SessionRecordSummary(
    long Id,
    Guid ProjectId,
    string SessionId,
    string Title,
    int MessageCount,
    DateTime CreateTime,
    DateTime? UpdateTime);

public record SessionRecordDetails(
    long Id,
    Guid ProjectId,
    string SessionId,
    string Title,
    IReadOnlyList<AiMessage> Messages,
    DateTime CreateTime,
    DateTime? UpdateTime);

public record SessionRecordTitleUpdateRequest(string Title);
