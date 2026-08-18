namespace Agw.Tools.Contracts.UserMemories;

public sealed record UserMemorySummaryResponse(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreateTime,
    DateTimeOffset? UpdateTime
);

public sealed record UserMemoryDetailResponse(
    Guid Id,
    string Name,
    string? Description,
    string Content,
    DateTimeOffset CreateTime,
    DateTimeOffset? UpdateTime
);

public sealed record UserMemoryCreateRequest(string Name, string? Description, string Content);

public sealed record UserMemoryUpdateRequest(Guid Id, string Name, string? Description, string Content);
