using System.Security.Cryptography;
using System.Text;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Shared;
using Agw.Shared.Data.Entities.Auth;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Auth;

public sealed class EfApiTokenStore : IApiTokenStore, ILegacyApiTokenImporter
{
    private const int PrefixLength = 12;

    private readonly AgwDbContext _context;
    private readonly IUserInfoService? _userInfoService;

    public EfApiTokenStore(AgwDbContext context, IUserInfoService? userInfoService = null)
    {
        _context = context;
        _userInfoService = userInfoService;
    }

    public async Task<IReadOnlyList<ApiTokenSummary>> ListTokensAsync(CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        var tokens = await _context
            .ApiTokens.AsNoTracking()
            .Where(token => token.CreateBy == ownerUserId)
            .Select(token => new ApiTokenSummary(token.Id, token.Name, token.Prefix, token.CreateTime))
            .ToArrayAsync(cancellationToken);
        return tokens.OrderByDescending(token => token.CreatedAt).ToArray();
    }

    public async Task<CreatedApiToken> CreateTokenAsync(string name, CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        var normalizedName = ApiToken.NormalizeName(name);
        if (
            await _context.ApiTokens.AnyAsync(
                token => token.NormalizedName == normalizedName && token.CreateBy == ownerUserId,
                cancellationToken
            )
        )
        {
            throw new AgwException(ErrorCodes.ApiTokenNameAlreadyExists);
        }

        var secret = CreateSecret();
        var token = new ApiToken
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            NormalizedName = normalizedName,
            Prefix = secret[..Math.Min(secret.Length, PrefixLength)],
            SecretHash = Hash(secret),
            CreateBy = ownerUserId,
        };
        _context.ApiTokens.Add(token);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            if (
                await _context.ApiTokens.AnyAsync(
                    existing => existing.NormalizedName == normalizedName && existing.CreateBy == ownerUserId,
                    cancellationToken
                )
            )
            {
                throw new AgwException(ErrorCodes.ApiTokenNameAlreadyExists);
            }

            throw;
        }

        return new CreatedApiToken(token.Id, token.Name, token.Prefix, token.CreateTime, secret);
    }

    public async Task<bool> RevokeTokenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        var token = await _context.ApiTokens.FirstOrDefaultAsync(
            candidate => candidate.Id == id && candidate.CreateBy == ownerUserId,
            cancellationToken
        );
        if (token == null)
            return false;

        _context.ApiTokens.Remove(token);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ApiTokenIdentity?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("agw_", StringComparison.Ordinal))
        {
            return null;
        }

        var prefix = token[..Math.Min(token.Length, PrefixLength)];
        var candidates = await _context
            .ApiTokens.AsNoTracking()
            .IgnoreUserScope()
            .Where(candidate => candidate.Prefix == prefix)
            .Select(candidate => new { candidate.SecretHash, candidate.CreateBy })
            .ToArrayAsync(cancellationToken);
        var candidateHash = Convert.FromHexString(Hash(token));

        foreach (var candidate in candidates)
        {
            byte[] storedHash;
            try
            {
                storedHash = Convert.FromHexString(candidate.SecretHash);
            }
            catch (FormatException)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(candidateHash, storedHash))
            {
                if (!string.IsNullOrWhiteSpace(candidate.CreateBy))
                {
                    return new ApiTokenIdentity(candidate.CreateBy.Trim());
                }
            }
        }

        return null;
    }

    public async Task ImportAsync(
        IReadOnlyList<LegacyApiTokenImport> tokens,
        CancellationToken cancellationToken = default
    )
    {
        var existingTokens = await LoadExistingTokensAsync(tokens, cancellationToken).ConfigureAwait(false);
        foreach (var token in tokens)
        {
            if (existingTokens.TryGetValue(token.Id, out var existingToken))
            {
                EnsureSameLegacyToken(existingToken, token);
                continue;
            }

            _context.ApiTokens.Add(
                new ApiToken
                {
                    Id = token.Id,
                    Name = token.Name,
                    NormalizedName = ApiToken.NormalizeName(token.Name),
                    Prefix = token.Prefix,
                    SecretHash = token.SecretHash,
                    CreateBy = Constants.AdminUserId,
                    CreateTime = token.CreatedAt,
                }
            );
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            existingTokens = await LoadExistingTokensAsync(tokens, cancellationToken).ConfigureAwait(false);
            if (
                !tokens.All(token =>
                    existingTokens.TryGetValue(token.Id, out var existing) && IsSameLegacyToken(existing, token)
                )
            )
            {
                throw;
            }
        }
    }

    private async Task<Dictionary<Guid, ApiToken>> LoadExistingTokensAsync(
        IReadOnlyList<LegacyApiTokenImport> tokens,
        CancellationToken cancellationToken
    )
    {
        var ids = tokens.Select(token => token.Id).ToArray();
        return await _context
            .ApiTokens.AsNoTracking()
            .Where(token => ids.Contains(token.Id))
            .ToDictionaryAsync(token => token.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void EnsureSameLegacyToken(ApiToken existingToken, LegacyApiTokenImport token)
    {
        if (!IsSameLegacyToken(existingToken, token))
        {
            throw new AgwException(
                ErrorCodes.LegacyApiTokenConflict,
                $"API Token '{token.Id}' conflicts with its legacy server-state record."
            );
        }
    }

    private static bool IsSameLegacyToken(ApiToken existingToken, LegacyApiTokenImport token)
    {
        return string.Equals(existingToken.NormalizedName, ApiToken.NormalizeName(token.Name), StringComparison.Ordinal)
            && string.Equals(existingToken.Prefix, token.Prefix, StringComparison.Ordinal)
            && string.Equals(existingToken.SecretHash, token.SecretHash, StringComparison.Ordinal);
    }

    private static string CreateSecret()
    {
        var value = Convert
            .ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"agw_{value}";
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private string ResolveOwnerUserId() => _userInfoService?.RequiredUserId ?? UserInfoUtil.RequiredUserId;
}
