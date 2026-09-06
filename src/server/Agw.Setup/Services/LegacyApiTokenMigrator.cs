using Agw.Auth.Contracts;
using Microsoft.Extensions.Logging;

namespace Agw.Setup.Services;

public sealed class LegacyApiTokenMigrator
{
    private readonly JsonInitializationStateStore _stateStore;
    private readonly ILegacyApiTokenImporter _tokenImporter;
    private readonly ILogger<LegacyApiTokenMigrator> _logger;

    public LegacyApiTokenMigrator(
        JsonInitializationStateStore stateStore,
        ILegacyApiTokenImporter tokenImporter,
        ILogger<LegacyApiTokenMigrator> logger
    )
    {
        _stateStore = stateStore;
        _tokenImporter = tokenImporter;
        _logger = logger;
    }

    public async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        using var systemScope = UserInfoUtil.PushSystemScope();
        var legacyTokens = _stateStore.GetLegacyApiTokens();
        if (!_stateStore.HasLegacyApiTokenSection)
            return 0;
        if (legacyTokens.Count == 0)
        {
            await _stateStore.ClearLegacyApiTokensAsync(cancellationToken);
            return 0;
        }

        var imports = legacyTokens
            .Select(token => new LegacyApiTokenImport(
                token.Id,
                token.Name,
                token.Prefix,
                token.SecretHash,
                token.CreatedAt
            ))
            .ToArray();
        await _tokenImporter.ImportAsync(imports, cancellationToken).ConfigureAwait(false);

        await _stateStore.ClearLegacyApiTokensAsync(cancellationToken);
        _logger.LogInformation(
            "Migrated {TokenCount} API Tokens from server-state.json to the database",
            legacyTokens.Count
        );
        return legacyTokens.Count;
    }
}
