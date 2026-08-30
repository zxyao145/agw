namespace Agw.Auth.Contracts;

public sealed record LoginRequest(string Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string NewPassword);

public sealed record CreateTokenRequest(string Name);

public sealed record SessionResponse(bool Authenticated, string AccessMode, int ApiMajorVersion, string? UserId);

public sealed record AntiforgeryResponse(string? RequestToken);
