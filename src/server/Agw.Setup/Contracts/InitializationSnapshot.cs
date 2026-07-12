namespace Agw.Setup.Contracts;

public record InitializationSnapshot(
    bool IsInitialized,
    string? PasswordHash,
    int SessionVersion,
    IReadOnlyList<ApiTokenSummary> Tokens);
