using Microsoft.AspNetCore.Http;

namespace Agw.Manager.Api.Contracts;

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
    IReadOnlyList<Guid> AgentIds,
    DateTime CreateTime,
    string? CreateBy,
    DateTime? UpdateTime,
    string? UpdateBy);
