using System.Buffers;
using System.Security.Cryptography;
using Agw.Auth.Application.Persistence;
using Agw.Auth.Contracts;
using Agw.Auth.Security;
using Agw.Infrastructure.Data;
using Agw.Shared.Utils;
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
            .Select(candidate => new TokenCandidate(candidate.Id, candidate.SecretHash, candidate.CreateBy))
            .ToArrayAsync(cancellationToken);
        if (FindMatch(token, candidates) is not { } match)
        {
            return null;
        }

        var userId = match.CreateBy!.Trim();
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
            return new ApiTokenIdentity(userId, match.Id, user.DisplayName, provider);
        }
        return new ApiTokenIdentity(userId, match.Id);
    }

    /// <summary>
    /// 在栈上计算并比较哈希，返回第一个哈希一致且有创建者的候选。
    /// Computes and compares hashes on the stack, returning the first candidate with a matching hash and a creator.
    /// </summary>
    private static TokenCandidate? FindMatch(string token, IReadOnlyList<TokenCandidate> candidates)
    {
        Span<byte> candidateHash = stackalloc byte[SHA256.HashSizeInBytes];
        Sha256Util.HashUtf8(token, candidateHash);
        Span<byte> storedHash = stackalloc byte[SHA256.HashSizeInBytes];
        foreach (var candidate in candidates)
        {
            if (
                !string.IsNullOrWhiteSpace(candidate.CreateBy)
                && Convert.FromHexString(candidate.SecretHash, storedHash, out _, out var written)
                    == OperationStatus.Done
                && written == storedHash.Length
                && CryptographicOperations.FixedTimeEquals(candidateHash, storedHash)
            )
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed record TokenCandidate(Guid Id, string SecretHash, string? CreateBy);
}
