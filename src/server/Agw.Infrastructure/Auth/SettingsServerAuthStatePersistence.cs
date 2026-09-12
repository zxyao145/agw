using Agw.Auth.Contracts;
using Agw.Settings.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Agw.Infrastructure.Auth;

/// <summary>Maps administrator state to the global auth configuration group.</summary>
public sealed class SettingsServerAuthStatePersistence : IServerAuthStatePersistence
{
    private const string Key = "auth";
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _clock;

    public SettingsServerAuthStatePersistence(ISettingsStore settings, TimeProvider clock)
    {
        _settings = settings;
        _clock = clock;
    }

    public async Task<AuthenticationSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var state = await ReadSettingsAsync(cancellationToken);
        return state == null ? null : new AuthenticationSnapshot(state.Value.PasswordHash, state.Value.SessionVersion);
    }

    public async Task InitializeAsync(string passwordHash, CancellationToken cancellationToken = default)
    {
        using var systemScope = UserInfoUtil.PushSystemScope();
        if (await ReadSettingsAsync(cancellationToken) != null)
            return;
        await _settings.TryCreateGlobalAsync(
            Key,
            new AuthenticationSettings
            {
                PasswordHash = passwordHash,
                SessionVersion = 1,
                InitializedAt = _clock.GetUtcNow(),
            },
            cancellationToken
        );
        // A concurrent winner must be valid; malformed data must never be overwritten by setup.
        await ReadSettingsAsync(cancellationToken);
    }

    public async Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default)
    {
        var snapshot =
            await ReadSettingsAsync(cancellationToken)
            ?? throw new AgwException(ErrorCodes.InvalidSetupConfiguration, "Server initialization has not completed.");
        var next = new AuthenticationSettings
        {
            PasswordHash = passwordHash,
            SessionVersion = checked(snapshot.Value.SessionVersion + 1),
            InitializedAt = snapshot.Value.InitializedAt,
        };
        if (!await _settings.TryUpdateGlobalAsync(Key, next, snapshot.Version, cancellationToken))
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The administrator password changed concurrently. Sign in again before retrying."
            );
    }

    private async Task<SettingSnapshot<AuthenticationSettings>?> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        SettingSnapshot<AuthenticationSettings>? snapshot;
        try
        {
            snapshot = await _settings.GetGlobalAsync<AuthenticationSettings>(Key, cancellationToken);
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode == 1
                && exception.Message.Contains("no such table: setting", StringComparison.Ordinal)
            )
        {
            return null;
        }
        catch (PostgresException exception)
            when (exception.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidCatalogName)
        {
            return null;
        }
        if (
            snapshot != null
            && (
                string.IsNullOrWhiteSpace(snapshot.Value.PasswordHash)
                || snapshot.Value.SessionVersion < 1
                || snapshot.Value.InitializedAt == default
            )
        )
            throw new AgwException(ErrorCodes.InvalidSetupConfiguration, "The administrator configuration is invalid.");
        return snapshot;
    }

    // Not a record: generated ToString must not reveal a password hash.
    private sealed class AuthenticationSettings
    {
        public string PasswordHash { get; set; } = string.Empty;
        public int SessionVersion { get; set; }
        public DateTimeOffset InitializedAt { get; set; }
    }
}
