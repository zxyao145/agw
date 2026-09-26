using System.Security.Cryptography;
using Agw.Auth.Application.Persistence;
using Agw.Auth.Contracts;
using Agw.Auth.Security;
using Agw.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Auth;

public sealed class EfApiTokenCredentialReader : IApiTokenCredentialReader
{
    private readonly AgwDbContext _context;

    public EfApiTokenCredentialReader(AgwDbContext context)
    {
        _context = context;
    }

    public async Task<ApiTokenIdentity?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (
            string.IsNullOrWhiteSpace(token) || !token.StartsWith(ApiTokenSecret.SecretPrefix, StringComparison.Ordinal)
        )
        {
            return null;
        }

        var prefix = ApiTokenSecret.GetLookupPrefix(token);
        var candidates = await _context
            .ApiTokens.AsNoTracking()
            .IgnoreUserScope()
            .Where(candidate => candidate.Prefix == prefix)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.SecretHash,
                candidate.CreateBy,
            })
            .ToArrayAsync(cancellationToken);
        var candidateHash = Convert.FromHexString(ApiTokenSecret.Hash(token));

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
                    var userId = candidate.CreateBy.Trim();
                    if (long.TryParse(userId, out var id) && id >= 10000)
                    {
                        var user = await _context
                            .AuthUsers.AsNoTracking()
                            .IgnoreUserScope()
                            .SingleOrDefaultAsync(user => user.Id == id, cancellationToken);
                        if (user == null)
                            return null;
                        var provider = await _context
                            .AuthExternalIdentities.AsNoTracking()
                            .IgnoreUserScope()
                            .Where(identity => identity.UserId == id)
                            .Select(identity => identity.ProviderId)
                            .SingleOrDefaultAsync(cancellationToken);
                        return new ApiTokenIdentity(userId, candidate.Id, user.DisplayName, provider);
                    }
                    return new ApiTokenIdentity(userId, candidate.Id);
                }
            }
        }

        return null;
    }
}
