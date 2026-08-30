using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Projects.Contracts;

public record TaskCreateRequest(Guid? JobId, string Input, string? Title = null, string? ContextId = null);

public sealed record ProjectConversationCreateRequest(string? ContextId = null);

public record ProjectConversationTitleUpdateRequest(string Title);

[JsonConverter(typeof(JsonStringEnumConverter<ProjectConversationMessageDirection>))]
public enum ProjectConversationMessageDirection
{
    [JsonStringEnumMemberName("newer")]
    Newer,

    [JsonStringEnumMemberName("older")]
    Older,
}

public sealed record ProjectConversationMessagesQuery
{
    [FromQuery(Name = "direction")]
    public ProjectConversationMessageDirection Direction { get; init; } = ProjectConversationMessageDirection.Newer;

    [FromQuery(Name = "cursor")]
    public string? Cursor { get; init; }

    [FromQuery(Name = "pageSize")]
    [Range(1, 100)]
    public int PageSize { get; init; } = 50;
}

public record ProjectConversationSummaryResponse(
    string ProjectId,
    Guid ConversationId,
    string ContextId,
    Guid? JobId,
    string Title,
    TaskExecutionStatus? LatestStatus,
    int ExecutionCount,
    int MessageCount,
    DateTimeOffset CreateTime,
    DateTimeOffset? UpdateTime,
    string? ErrorMessage
);

public record ProjectConversationResponse(
    string ProjectId,
    Guid ConversationId,
    string ContextId,
    Guid? JobId,
    string Title,
    TaskExecutionStatus? LatestStatus,
    int ExecutionCount,
    int MessageCount,
    DateTimeOffset CreateTime,
    DateTimeOffset? UpdateTime,
    string? ErrorMessage,
    ProjectConversationUsage Usage,
    ProjectConversationResumeStateResponse? ResumeState
);

public sealed record ProjectConversationUsage
{
    public long InputTokenCount { get; init; }

    public long OutputTokenCount { get; init; }

    public long TotalTokenCount { get; init; }

    public long CachedInputTokenCount { get; init; }

    public long ReasoningTokenCount { get; init; }
}

public sealed record ProjectConversationResumeStateResponse(string? TargetType, string? TargetId, string? AgentMode);

public sealed record ProjectConversationMessagePageResponse(
    IReadOnlyList<AgwMessage> Items,
    string? NextCursor,
    bool HasMore
);
