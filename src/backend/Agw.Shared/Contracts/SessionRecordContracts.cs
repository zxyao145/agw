using Agw.Shared.Models;

namespace Agw.Shared.Contracts;

public record SessionRecordSummary(
    Guid Id,
    string ProjectId,
    string SessionId,
    string Title,
    int MessageCount,
    DateTime CreateTime,
    DateTime? UpdateTime);

public record SessionRecordDetails(
    Guid Id,
    string ProjectId,
    string SessionId,
    string Title,
    IReadOnlyList<AiMessage> Messages,
    DateTime CreateTime,
    DateTime? UpdateTime);

public record SessionRecordTitleUpdateRequest(string Title);
