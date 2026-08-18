using Agw.Shared.Data.Entities.Skills;
using Microsoft.AspNetCore.Http;

namespace Agw.Skills.Contracts.Manager;

public sealed class SkillCreateRequest
{
    public SkillKind Kind { get; set; } = SkillKind.Local;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IFormFile? Archive { get; set; }
    public string? RemoteUrl { get; set; }
}

public sealed class SkillUpdateRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IFormFile? Archive { get; set; }
    public string? RemoteUrl { get; set; }
}

public sealed record SkillResponse(
    Guid Id,
    string Name,
    string Description,
    SkillKind Kind,
    string ContentPath,
    string? RemoteUrl,
    bool IsBuiltIn,
    IReadOnlyList<Guid> AgentIds,
    DateTimeOffset CreateTime,
    string? CreateBy,
    DateTimeOffset? UpdateTime,
    string? UpdateBy
);
