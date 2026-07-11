namespace Agw.Setup.Contracts;

public sealed record ApiTokenSummary(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt);

public sealed record CreatedApiToken(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, string Token);
