using Agw.Extensions.Contracts;
using Agw.Settings.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Host.Runtime;

public sealed class ExtensionDeploymentIdentity : IExtensionDeploymentIdentity
{
    private readonly ISettingsStore _settings;

    public ExtensionDeploymentIdentity(ISettingsStore settings)
    {
        _settings = settings;
    }

    public async Task<string> GetServerIdAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _settings.GetGlobalAsync<ExtensionDeploymentSettings>(
                ExtensionDeploymentSettings.Key,
                cancellationToken
            );
            return Validate(existing?.Value.ServerId);
        }
        catch (AgwException exception) when (exception.Code == ErrorCodes.InvalidParam.Code)
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable, "Persisted Server identity is invalid.");
        }
    }

    private static string Validate(string? id)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed) || parsed.Version != 7 || parsed.ToString() != id)
            throw new AgwException(ErrorCodes.ExtensionUnavailable, "Persisted Server identity is missing or invalid.");
        return id!;
    }
}
