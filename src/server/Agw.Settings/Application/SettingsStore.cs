using System.Text.Json;
using Agw.Settings.Application.Persistence;
using Agw.Settings.Contracts;
using Agw.Shared.Contracts;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;

namespace Agw.Settings.Application;

public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = WebJsonOptions.Default;
    private readonly ISettingsPersistence _persistence;
    private readonly ICurrentUser _currentUser;

    public SettingsStore(ISettingsPersistence persistence, ICurrentUser currentUser)
    {
        _persistence = persistence;
        _currentUser = currentUser;
    }

    public Task<SettingSnapshot<T>?> GetGlobalAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class => ReadAsync<T>(key, null, cancellationToken);

    public Task<SettingSnapshot<T>?> GetCurrentUserAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class => ReadAsync<T>(key, _currentUser.RequiredUserId, cancellationToken);

    public Task<bool> TryCreateGlobalAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        where T : class => CreateAsync(key, null, value, cancellationToken);

    public Task<bool> TryCreateCurrentUserAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        where T : class => CreateAsync(key, _currentUser.RequiredUserId, value, cancellationToken);

    public Task<bool> TryUpdateGlobalAsync<T>(
        string key,
        T value,
        long expectedVersion,
        CancellationToken cancellationToken = default
    )
        where T : class => UpdateAsync(key, null, value, expectedVersion, cancellationToken);

    public Task<bool> TryUpdateCurrentUserAsync<T>(
        string key,
        T value,
        long expectedVersion,
        CancellationToken cancellationToken = default
    )
        where T : class => UpdateAsync(key, _currentUser.RequiredUserId, value, expectedVersion, cancellationToken);

    private async Task<SettingSnapshot<T>?> ReadAsync<T>(
        string key,
        string? userId,
        CancellationToken cancellationToken
    )
        where T : class
    {
        ValidateKey(key);
        var document = await _persistence.ReadAsync(key, userId, cancellationToken);
        if (document == null)
            return null;
        try
        {
            using var json = JsonDocument.Parse(document.ValueJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object || document.Version < 1)
                throw InvalidValue();
            var value = json.RootElement.Deserialize<T>(JsonOptions) ?? throw InvalidValue();
            return new SettingSnapshot<T> { Value = value, Version = document.Version };
        }
        catch (JsonException)
        {
            // Values may contain credentials. Do not include the JSON or parser exception in errors.
            throw InvalidValue();
        }
    }

    private Task<bool> CreateAsync<T>(string key, string? userId, T value, CancellationToken cancellationToken)
        where T : class
    {
        ValidateKey(key);
        return _persistence.TryCreateAsync(key, userId, Serialize(value), cancellationToken);
    }

    private Task<bool> UpdateAsync<T>(
        string key,
        string? userId,
        T value,
        long expectedVersion,
        CancellationToken cancellationToken
    )
        where T : class
    {
        ValidateKey(key);
        if (expectedVersion < 1 || expectedVersion == long.MaxValue)
            throw new AgwException(ErrorCodes.InvalidParam, "A valid settings version is required.");
        return _persistence.TryUpdateAsync(key, userId, Serialize(value), expectedVersion, cancellationToken);
    }

    private static string Serialize<T>(T value)
        where T : class
    {
        var json = JsonSerializer.SerializeToElement(value, JsonOptions);
        if (json.ValueKind != JsonValueKind.Object)
            throw InvalidValue();
        return json.GetRawText();
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key != key.Trim())
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "A settings key of 1–128 characters without surrounding whitespace is required."
            );
    }

    private static AgwException InvalidValue() =>
        new(ErrorCodes.InvalidParam, "The configuration group contains invalid data.");
}
