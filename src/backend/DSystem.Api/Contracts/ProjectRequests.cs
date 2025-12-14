namespace DSystem.Api.Contracts;

public record ProjectCreateRequest(string Name, string? Description, bool Enable);

public record ProjectUpdateRequest(string Name, string? Description, bool Enable);