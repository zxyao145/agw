namespace Agw.Shared.Contracts;

public record ProjectCreateRequest(string Name, string? Description, string? Workspace, bool Enable, string? ExtraSetting);

public record ProjectUpdateRequest(string Name, string? Description, string? Workspace, bool Enable, string? ExtraSetting);
