using Agw.Infrastructure.Data;
using Agw.Shared;
using Agw.Shared.Data.Entities.Auth;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agw.Setup.Services;

public sealed class LegacyApiTokenMigrator
{
    private readonly JsonInitializationStateStore _stateStore;
    private readonly AgwDbContext _context;
    private readonly ILogger<LegacyApiTokenMigrator> _logger;

    public LegacyApiTokenMigrator(
        JsonInitializationStateStore stateStore,
        AgwDbContext context,
        ILogger<LegacyApiTokenMigrator> logger)
    {
        _stateStore = stateStore;
        _context = context;
        _logger = logger;
    }

    public async Task<int> MigrateAsync(
        CancellationToken cancellationToken = default)
    {
        var legacyTokens = _stateStore.GetLegacyApiTokens();
        if (!_stateStore.HasLegacyApiTokenSection) return 0;
        if (legacyTokens.Count == 0)
        {
            await _stateStore.ClearLegacyApiTokensAsync(cancellationToken);
            return 0;
        }

        var existingTokens = await LoadExistingTokensAsync(legacyTokens, cancellationToken);
        foreach (var legacyToken in legacyTokens)
        {
            if (existingTokens.TryGetValue(legacyToken.Id, out var existingToken))
            {
                EnsureSameToken(existingToken, legacyToken);
                continue;
            }

            _context.ApiTokens.Add(new ApiToken
            {
                Id = legacyToken.Id,
                Name = legacyToken.Name,
                NormalizedName = ApiToken.NormalizeName(legacyToken.Name),
                Prefix = legacyToken.Prefix,
                SecretHash = legacyToken.SecretHash,
                CreateBy = Constants.AdminUserId,
                CreateTime = legacyToken.CreatedAt
            });
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            existingTokens = await LoadExistingTokensAsync(legacyTokens, cancellationToken);
            if (!legacyTokens.All(token =>
                    existingTokens.TryGetValue(token.Id, out var existing)
                    && IsSameToken(existing, token)))
            {
                throw;
            }
        }

        await _stateStore.ClearLegacyApiTokensAsync(cancellationToken);
        _logger.LogInformation(
            "Migrated {TokenCount} API Tokens from server-state.json to the database",
            legacyTokens.Count);
        return legacyTokens.Count;
    }

    private async Task<Dictionary<Guid, ApiToken>> LoadExistingTokensAsync(
        IReadOnlyList<LegacyApiTokenState> legacyTokens,
        CancellationToken cancellationToken)
    {
        var ids = legacyTokens.Select(token => token.Id).ToArray();
        return await _context.ApiTokens
            .AsNoTracking()
            .Where(token => ids.Contains(token.Id))
            .ToDictionaryAsync(token => token.Id, cancellationToken);
    }

    private static void EnsureSameToken(
        ApiToken existingToken,
        LegacyApiTokenState legacyToken)
    {
        if (!IsSameToken(existingToken, legacyToken))
        {
            throw new InvalidOperationException(
                $"API Token '{legacyToken.Id}' conflicts with its legacy server-state record.");
        }
    }

    private static bool IsSameToken(
        ApiToken existingToken,
        LegacyApiTokenState legacyToken)
    {
        return string.Equals(
                   existingToken.NormalizedName,
                   ApiToken.NormalizeName(legacyToken.Name),
                   StringComparison.Ordinal)
               && string.Equals(
                   existingToken.Prefix,
                   legacyToken.Prefix,
                   StringComparison.Ordinal)
               && string.Equals(
                   existingToken.SecretHash,
                   legacyToken.SecretHash,
                   StringComparison.Ordinal);
    }
}
