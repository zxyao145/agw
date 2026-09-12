namespace Agw.Settings.Contracts;

/// <summary>Internal configuration groups. Global access is for trusted application use cases only.</summary>
public interface ISettingsStore
{
    Task<SettingSnapshot<T>?> GetGlobalAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class;
    Task<SettingSnapshot<T>?> GetCurrentUserAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class;
    Task<bool> TryCreateGlobalAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        where T : class;
    Task<bool> TryCreateCurrentUserAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        where T : class;
    Task<bool> TryUpdateGlobalAsync<T>(
        string key,
        T value,
        long expectedVersion,
        CancellationToken cancellationToken = default
    )
        where T : class;
    Task<bool> TryUpdateCurrentUserAsync<T>(
        string key,
        T value,
        long expectedVersion,
        CancellationToken cancellationToken = default
    )
        where T : class;
}

public sealed class SettingSnapshot<T>
    where T : class
{
    public required T Value { get; init; }
    public required long Version { get; init; }
}
