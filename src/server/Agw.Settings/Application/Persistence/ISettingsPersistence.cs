using Agw.Settings.Contracts;

namespace Agw.Settings.Application.Persistence;

public interface ISettingsPersistence
{
    Task<SettingDocument?> ReadAsync(string key, string? userId, CancellationToken cancellationToken);
    Task<bool> TryCreateAsync(string key, string? userId, string valueJson, CancellationToken cancellationToken);
    Task<bool> TryUpdateAsync(
        string key,
        string? userId,
        string valueJson,
        long expectedVersion,
        CancellationToken cancellationToken
    );
}
