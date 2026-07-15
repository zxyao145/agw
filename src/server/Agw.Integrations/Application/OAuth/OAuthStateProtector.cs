using System.Security.Cryptography;
using System.Text.Json;

using Agw.Shared.Exceptions;

using Microsoft.AspNetCore.DataProtection;

namespace Agw.Integrations.Application.OAuth;

public sealed class OAuthStateProtector
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private const string Purpose = "Agw.Integrations.OAuthState.v1";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeProvider _timeProvider;

    public OAuthStateProtector(IDataProtectionProvider dataProtectionProvider, TimeProvider timeProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _timeProvider = timeProvider;
    }

    public string Protect(Guid connectionId, string? pkceVerifier, string returnPath)
    {
        ValidateReturnPath(returnPath);
        var state = new OAuthCallbackState
        {
            ConnectionId = connectionId,
            PkceVerifier = pkceVerifier,
            ReturnPath = returnPath,
            ExpiresAtUtc = _timeProvider.GetUtcNow().Add(StateLifetime)
        };
        var payload = JsonSerializer.Serialize(state);
        return _protector.Protect(payload, StateLifetime);
    }

    public bool TryUnprotect(string? protectedState, out OAuthCallbackState? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(protectedState))
        {
            return false;
        }

        try
        {
            var payload = _protector.Unprotect(protectedState);
            var candidate = JsonSerializer.Deserialize<OAuthCallbackState>(payload);
            if (candidate == null
                || candidate.ConnectionId == Guid.Empty
                || candidate.ExpiresAtUtc <= _timeProvider.GetUtcNow()
                || !IsSafeReturnPath(candidate.ReturnPath))
            {
                return false;
            }

            state = candidate;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return false;
        }
    }

    public static void ValidateReturnPath(string? returnPath)
    {
        if (!IsSafeReturnPath(returnPath))
        {
            throw new AgwException(ErrorCodes.OAuthReturnPathInvalid);
        }
    }

    private static bool IsSafeReturnPath(string? returnPath)
    {
        if (string.IsNullOrWhiteSpace(returnPath)
            || returnPath[0] != '/'
            || (returnPath.Length > 1 && (returnPath[1] == '/' || returnPath[1] == '\\'))
            || returnPath.Contains('\\')
            || returnPath.Any(char.IsControl)
            || !Uri.TryCreate(returnPath, UriKind.Relative, out _))
        {
            return false;
        }

        var decoded = returnPath;
        for (var index = 0; index < 2; index++)
        {
            try
            {
                decoded = Uri.UnescapeDataString(decoded);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (decoded.Length == 0
                || decoded[0] != '/'
                || (decoded.Length > 1 && (decoded[1] == '/' || decoded[1] == '\\'))
                || decoded.Contains('\\')
                || decoded.Any(char.IsControl))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class OAuthCallbackState
{
    public Guid ConnectionId { get; set; }
    public string? PkceVerifier { get; set; }
    public string ReturnPath { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
