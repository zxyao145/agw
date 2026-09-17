using System.Globalization;
using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Auth.Security;
using Agw.Infrastructure.Data;
using Agw.Projects.Contracts;
using Agw.Shared.Data.Entities.Auth;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Auth;

/// <summary>
/// Authentication-boundary access: only verified external identities, signed local principals,
/// or proof-bound one-time codes may enter these operations. No general cross-user API is exposed.
/// </summary>
public sealed class EfOidcIdentityStore : IOidcIdentityStore
{
    private readonly AgwDbContext _context;
    private readonly IUserProjectInitializer _projects;
    private readonly IApiTokenStore _tokens;
    private readonly TimeProvider _timeProvider;
    private readonly IOidcProviderAvailability _providers;

    public EfOidcIdentityStore(
        AgwDbContext context,
        IUserProjectInitializer projects,
        IApiTokenStore tokens,
        TimeProvider timeProvider,
        IOidcProviderAvailability providers
    )
    {
        _context = context;
        _projects = projects;
        _tokens = tokens;
        _timeProvider = timeProvider;
        _providers = providers;
    }

    public async Task<OidcUser> ResolveAsync(
        VerifiedOidcIdentity identity,
        CancellationToken cancellationToken = default
    )
    {
        if (
            string.IsNullOrWhiteSpace(identity.Issuer)
            || identity.Issuer.Length > 512
            || string.IsNullOrWhiteSpace(identity.Subject)
            || identity.Subject.Length > 255
        )
            throw new AgwException(ErrorCodes.AuthenticationRequired);

        var existingId = await FindIdentityAsync(identity, cancellationToken);
        if (existingId.HasValue)
            return await RefreshProfileAsync(existingId.Value, identity, cancellationToken);

        // The first operation writes the allocator row, serializing concurrent registrations
        // without a SQLite read-to-write lock upgrade. No network or filesystem I/O occurs here.
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var allocated = await _context.Database.ExecuteSqlRawAsync(
            "UPDATE auth_user_id_sequence SET next_id = next_id + 1 WHERE id = 1",
            cancellationToken
        );
        if (allocated != 1)
            throw new AgwException(ErrorCodes.ServerNotInitialized);

        existingId = await FindIdentityAsync(identity, cancellationToken);
        if (existingId.HasValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await RefreshProfileAsync(existingId.Value, identity, cancellationToken);
        }

        var id = await _context
            .AuthUserIdSequences.AsNoTracking()
            .Where(sequence => sequence.Id == 1)
            .Select(sequence => sequence.NextId - 1)
            .SingleAsync(cancellationToken);
        var userId = id.ToString(CultureInfo.InvariantCulture);
        var user = new AuthUser
        {
            Id = id,
            DisplayName = DisplayName(identity.DisplayName, userId),
            Email = Email(identity.Email),
            SessionVersion = 1,
            CreateBy = userId,
        };
        var result = new OidcUser(userId, user.DisplayName, user.SessionVersion, identity.ProviderId);
        using var owner = PushOwner(result);
        _context.AuthUsers.Add(user);
        _context.AuthExternalIdentities.Add(
            new AuthExternalIdentity
            {
                UserId = id,
                ProviderId = identity.ProviderId,
                Issuer = identity.Issuer,
                Subject = identity.Subject,
                CreateBy = userId,
            }
        );
        await _context.SaveChangesAsync(cancellationToken);
        // Atomicity requires IAuthDbContext and IProjectsDbContext to resolve to the same scoped
        // AgwDbContext instance; splitting either context requires redesigning this transaction.
        await _projects.EnsureDefaultsAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<OidcUser?> ReadAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return null;
        return await (
            from user in _context.AuthUsers.AsNoTracking().IgnoreUserScope()
            where user.Id == id
            join external in _context.AuthExternalIdentities.AsNoTracking().IgnoreUserScope()
                on user.Id equals external.UserId
                into identities
            from external in identities.DefaultIfEmpty()
            select new OidcUser(
                userId,
                user.DisplayName,
                user.SessionVersion,
                external == null ? null : external.ProviderId
            )
        ).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<string> CreateDesktopGrantAsync(
        OidcUser user,
        string providerId,
        string codeChallenge,
        CancellationToken cancellationToken = default
    )
    {
        if (!DesktopLoginProof.IsChallenge(codeChallenge))
            throw new AgwException(ErrorCodes.DesktopLoginInvalid);
        var code = DesktopLoginProof.CreateCode();
        using var owner = PushOwner(user);
        _context.AuthDesktopLoginGrants.Add(
            new AuthDesktopLoginGrant
            {
                CodeHash = DesktopLoginProof.HashCode(code),
                UserId = long.Parse(user.UserId, CultureInfo.InvariantCulture),
                ProviderId = providerId,
                CodeChallenge = codeChallenge,
                SessionVersion = user.SessionVersion,
                ExpiresAt = _timeProvider.GetUtcNow().AddMinutes(2),
                CreateBy = user.UserId,
            }
        );
        await _context.SaveChangesAsync(cancellationToken);
        return code;
    }

    public async Task<DesktopExchangeResponse> ExchangeAsync(
        string code,
        string verifier,
        CancellationToken cancellationToken = default
    )
    {
        DesktopLoginProof.Validate(code, verifier);
        var hash = DesktopLoginProof.HashCode(code);
        var grant = await _context
            .AuthDesktopLoginGrants.AsNoTracking()
            .IgnoreUserScope()
            .SingleOrDefaultAsync(candidate => candidate.CodeHash == hash, cancellationToken);
        if (
            grant == null
            || grant.ExpiresAt <= _timeProvider.GetUtcNow()
            || !DesktopLoginProof.Matches(grant.CodeChallenge, verifier)
        )
            throw new AgwException(ErrorCodes.DesktopLoginInvalid);
        if (!_providers.IsEnabled(grant.ProviderId))
            throw new AgwException(ErrorCodes.DesktopLoginInvalid);
        var user = await ReadAsync(grant.UserId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        if (user == null || user.SessionVersion != grant.SessionVersion)
            throw new AgwException(ErrorCodes.DesktopLoginInvalid);

        using var owner = PushOwner(user);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var deleted = await _context
            .AuthDesktopLoginGrants.Where(candidate => candidate.CodeHash == hash && candidate.ExpiresAt > now)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted != 1)
            throw new AgwException(ErrorCodes.DesktopLoginInvalid);
        // Fence the user's version for the remainder of this transaction.
        var currentVersion = await _context
            .AuthUsers.Where(candidate =>
                candidate.Id == grant.UserId && candidate.SessionVersion == grant.SessionVersion
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters.SetProperty(candidate => candidate.SessionVersion, candidate => candidate.SessionVersion),
                cancellationToken
            );
        if (currentVersion != 1)
            throw new AgwException(ErrorCodes.DesktopLoginInvalid);
        var current = await ReadAsync(user.UserId, cancellationToken);
        if (current == null || current.SessionVersion != grant.SessionVersion)
            throw new AgwException(ErrorCodes.DesktopLoginInvalid);
        var token = await _tokens.CreateTokenAsync($"Desktop {Guid.CreateVersion7():N}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DesktopExchangeResponse(token.Token, token.Id, user.UserId, current.DisplayName, grant.ProviderId);
    }

    public async Task<int> DeleteExpiredGrantsAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var hashes = await _context
            .AuthDesktopLoginGrants.IgnoreUserScope()
            .Where(grant => grant.ExpiresAt <= now)
            .OrderBy(grant => grant.ExpiresAt)
            .Select(grant => grant.CodeHash)
            .Take(1000)
            .ToArrayAsync(cancellationToken);
        return await _context
            .AuthDesktopLoginGrants.IgnoreUserScope()
            .Where(grant => hashes.Contains(grant.CodeHash) && grant.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private Task<long?> FindIdentityAsync(VerifiedOidcIdentity identity, CancellationToken cancellationToken) =>
        _context
            .AuthExternalIdentities.AsNoTracking()
            .IgnoreUserScope()
            .Where(external => external.Issuer == identity.Issuer && external.Subject == identity.Subject)
            .Select(external => (long?)external.UserId)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<OidcUser> RefreshProfileAsync(
        long id,
        VerifiedOidcIdentity identity,
        CancellationToken cancellationToken
    )
    {
        var userId = id.ToString(CultureInfo.InvariantCulture);
        var current =
            await ReadAsync(userId, cancellationToken) ?? throw new AgwException(ErrorCodes.AuthenticationRequired);
        using var owner = PushOwner(current);
        var user = await _context.AuthUsers.SingleAsync(candidate => candidate.Id == id, cancellationToken);
        user.DisplayName = DisplayName(identity.DisplayName, userId);
        user.Email = Email(identity.Email);
        await _context.SaveChangesAsync(cancellationToken);
        return current with { DisplayName = user.DisplayName, ProviderId = identity.ProviderId };
    }

    private static IDisposable PushOwner(OidcUser user) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, user.UserId), new Claim(ClaimTypes.Name, user.DisplayName)],
                    "OidcProvisioning"
                )
            )
        );

    private static string DisplayName(string? value, string userId) =>
        string.IsNullOrWhiteSpace(value) ? $"User {userId}" : value.Trim()[..Math.Min(value.Trim().Length, 256)];

    private static string? Email(string? value) => value is { Length: > 0 and <= 320 } ? value : null;
}
