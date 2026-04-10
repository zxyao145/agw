namespace Agw.Integrations.Contracts.Manager;

public sealed record AppInstanceCreateRequest(
    string AppName,
    string ClientId,
    string ClientSecret,
    bool UsePkce);
