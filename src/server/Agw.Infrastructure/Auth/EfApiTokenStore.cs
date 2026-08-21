using System.Security.Cryptography;
using System.Text;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Shared;
using Agw.Shared.Data.Entities.Auth;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Auth;

public sealed class EfApiTokenStore : IApiTokenStore
{
    private const int PrefixLength = 12;

    private readonly AgwDbContext _context;

    public EfApiTokenStore(AgwDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<ApiTokenSummary>> ListTokensAsync(CancellationToken cancellationToken = default)
    {
        var tokens = await _context
            .ApiTokens.AsNoTracking()
            .Select(token => new ApiTokenSummary(token.Id, token.Name, token.Prefix, token.CreateTime))
            .ToArrayAsync(cancellationToken);
        return tokens.OrderByDescending(token => token.CreatedAt).ToArray();
    }

    public async Task<CreatedApiToken> CreateTokenAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalizedName = ApiToken.NormalizeName(name);
        if (await _context.ApiTokens.AnyAsync(token => token.NormalizedName == normalizedName, cancellationToken))
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
                    existing => existing.NormalizedName == normalizedName,
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
        var token = await _context.ApiTokens.FindAsync([id], cancellationToken);
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
                var userId = string.IsNullOrWhiteSpace(candidate.CreateBy)
                    ? Constants.AdminUserId
                    : candidate.CreateBy.Trim();
                return new ApiTokenIdentity(userId);
            }
        }

        return null;
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
}
