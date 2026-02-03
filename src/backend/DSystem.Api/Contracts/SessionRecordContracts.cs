using DSystem.Domain.Models;

namespace DSystem.Api.Contracts;

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
