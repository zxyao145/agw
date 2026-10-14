using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Settings.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Agw.Infrastructure.Auth;

/// <summary>Maps administrator state to the global auth configuration group.</summary>
public sealed class SettingsServerAuthStatePersistence : IServerAuthStatePersistence
{
    private const string Key = "auth";
    private readonly ISettingsStore _settings;
    private readonly AgwDbContext _dbContext;
    private readonly TimeProvider _clock;

    public SettingsServerAuthStatePersistence(ISettingsStore settings, AgwDbContext dbContext, TimeProvider clock)
    {
        _settings = settings;
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task<bool> StorageExistsAsync(CancellationToken cancellationToken = default)
    {
        // 先确认数据库存在：EF 把这一步的连接失败作为预期情况，只记录 Debug 日志；再检查 setting 表，不执行会失败的查询。
        // Checks the database first, where EF treats connection failures as expected and logs them at Debug;
        // then checks the setting table without running a query that fails.
        var database = _dbContext.Database;
        if (!await database.GetService<IRelationalDatabaseCreator>().ExistsAsync(cancellationToken))
            return false;

        var table = _dbContext.Settings.EntityType;
        var tableName = table.GetTableName()!;
        if (database.IsSqlite())
            return await database
                .SqlQuery<bool>(
                    $"SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = {tableName}) AS \"Value\""
                )
                .SingleAsync(cancellationToken);
        if (database.IsNpgsql())
        {
            var qualifiedName = database
                .GetService<ISqlGenerationHelper>()
                .DelimitIdentifier(tableName, table.GetSchema());
            return await database
                .SqlQuery<bool>($"SELECT to_regclass({qualifiedName}) IS NOT NULL AS \"Value\"")
                .SingleAsync(cancellationToken);
        }

        throw new AgwException(ErrorCodes.UnsupportedDatabaseProvider);
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
        var snapshot = await _settings.GetGlobalAsync<AuthenticationSettings>(Key, cancellationToken);
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
