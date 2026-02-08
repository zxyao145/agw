namespace DSystem.Shared.Contracts;

public record ProjectCreateRequest(string Name, string? Description, bool Enable, string? ExtraSetting);

public record ProjectUpdateRequest(string Name, string? Description, bool Enable, string? ExtraSetting);
