using Agw.Infrastructure.Data;
using Agw.Settings.Application.Persistence;
using Agw.Settings.Contracts;
using Agw.Shared.Data.Entities.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agw.Infrastructure.Settings;

public sealed class EfSettingsPersistence : ISettingsPersistence
{
    private readonly ISettingsDbContext _context;

    public EfSettingsPersistence(ISettingsDbContext context)
    {
        _context = context;
    }

    public Task<SettingDocument?> ReadAsync(string key, string? userId, CancellationToken cancellationToken) =>
        Query(key, userId)
            .AsNoTracking()
            .Select(setting => new SettingDocument { ValueJson = setting.ValueJson, Version = setting.Version })
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> TryCreateAsync(
        string key,
        string? userId,
        string valueJson,
        CancellationToken cancellationToken
    )
    {
        if (await Query(key, userId).AnyAsync(cancellationToken))
            return false;
        var setting = new Setting
        {
            Id = Guid.CreateVersion7(),
            Key = key,
            UserId = userId,
            ValueJson = valueJson,
            Version = 1,
        };
        _context.Settings.Add(setting);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            _context.Settings.Entry(setting).State = EntityState.Detached;
            if (await Query(key, userId).AnyAsync(cancellationToken))
                return false;
            throw;
        }
    }

    public async Task<bool> TryUpdateAsync(
        string key,
        string? userId,
        string valueJson,
        long expectedVersion,
        CancellationToken cancellationToken
    )
    {
        var setting = await Query(key, userId).SingleOrDefaultAsync(cancellationToken);
        if (setting == null || setting.Version != expectedVersion)
            return false;
        setting.ValueJson = valueJson;
        setting.Version = expectedVersion + 1;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            _context.Settings.Entry(setting).State = EntityState.Detached;
            return false;
        }
    }

    private IQueryable<Setting> Query(string key, string? userId)
    {
        // Only the explicitly global path bypasses user scope, and it always restricts UserId to NULL.
        var query = userId == null ? _context.Settings.IgnoreUserScope() : _context.Settings;
        return query.Where(setting => setting.Key == key && setting.UserId == userId);
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException
            is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
                or SqliteException { SqliteExtendedErrorCode: 2067 or 1555 };
}
