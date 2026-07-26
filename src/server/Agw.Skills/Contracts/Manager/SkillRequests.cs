using Microsoft.AspNetCore.Http;

namespace Agw.Skills.Contracts.Manager;

public sealed class SkillCreateRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IFormFile Archive { get; set; } = default!;
}

public sealed class SkillUpdateRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IFormFile? Archive { get; set; }
}

public sealed record SkillResponse(
    Guid Id,
    string Name,
    string Description,
    string ContentPath,
    bool IsBuiltIn,
    IReadOnlyList<Guid> AgentIds,
    DateTimeOffset CreateTime,
    string? CreateBy,
    DateTimeOffset? UpdateTime,
    string? UpdateBy);
