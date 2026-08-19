namespace Agw.Auth.Contracts;

public sealed record AuthenticationSnapshot(string? PasswordHash, int SessionVersion);
