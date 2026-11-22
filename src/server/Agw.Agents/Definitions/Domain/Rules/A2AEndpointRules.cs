using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Definitions.Domain.Rules;

public sealed record A2AOriginCredentialsInput(string Origin, IReadOnlyDictionary<string, string?> Headers);

public sealed record A2AEndpointInput(string? CardUrl, IReadOnlyList<A2AOriginCredentialsInput>? Credentials);

public sealed record A2ASelectedInterface(string Url, string ProtocolBinding);

public sealed record A2AEndpointSnapshot
{
    public required string CardUrl { get; init; }
    public required Dictionary<string, string> Credentials { get; init; }
    public required A2ASelectedInterface Interface { get; init; }
    public required bool Streaming { get; init; }
    public required AgentA2AEndpointExtra Extra { get; init; }
}

public static class A2AEndpointRules
{
    public const string EmptyExtra = "{}";

    private const int MaxUrlLength = 2048;
    private const int MaxOrigins = 2;
    private const int MaxHeadersPerOrigin = 20;
    private const int MaxHeaderValueLength = 8192;

    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Content-Type",
        "Transfer-Encoding",
        "Connection",
        "Accept",
        "A2A-Version",
        "A2A-Extensions",
    };

    public static Uri ValidateCardUrl(string? cardUrl) =>
        ParseEndpointUri(cardUrl)
        ?? throw new AgwException(
            ErrorCodes.InvalidParam,
            "The agent card URL must be an absolute HTTP or HTTPS URL without user info "
                + $"and at most {MaxUrlLength} characters."
        );

    public static string NormalizeOrigin(Uri uri)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        return uri.HostNameType == UriHostNameType.IPv6
            ? $"{uri.Scheme}://[{host}]:{uri.Port}"
            : $"{uri.Scheme}://{host}:{uri.Port}";
    }

    public static A2AEndpointCredentials ResolveCredentials(
        A2AEndpointCredentials? stored,
        IReadOnlyList<A2AOriginCredentialsInput>? requested
    )
    {
        if (requested is null)
        {
            return stored ?? A2AEndpointCredentials.Empty;
        }

        if (requested.Count > MaxOrigins)
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"A2A credentials support at most {MaxOrigins} origins.");
        }

        var credentials = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var group in requested)
        {
            var origin = NormalizeCredentialOrigin(group?.Origin);
            if (credentials.ContainsKey(origin))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "A2A credential origins must be unique.");
            }

            var previous = stored?.HeadersByOrigin.GetValueOrDefault(origin);
            credentials.Add(origin, ResolveHeaders(group?.Headers, previous));
        }

        return new A2AEndpointCredentials(credentials);
    }

    private static string NormalizeCredentialOrigin(string? address)
    {
        if (ParseEndpointUri(address) is { AbsolutePath: "/", Query: "", Fragment: "" } uri)
        {
            return NormalizeOrigin(uri);
        }

        throw new AgwException(
            ErrorCodes.InvalidParam,
            "Each A2A credential origin must contain only an HTTP or HTTPS scheme, host and optional port."
        );
    }

    private static Dictionary<string, string> ResolveHeaders(
        IReadOnlyDictionary<string, string?>? headers,
        IReadOnlyDictionary<string, string>? previous
    )
    {
        if (headers is not { Count: <= MaxHeadersPerOrigin })
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"An A2A credential origin supports at most {MaxHeadersPerOrigin} headers."
            );
        }

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (name.Length == 0 || !name.All(IsTokenCharacter) || ReservedHeaders.Contains(name))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    "An A2A credential header name is invalid or reserved."
                );
            }

            if (!resolved.TryAdd(name, ResolveHeaderValue(name, value, previous)))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    "A2A credential header names must be unique within each origin."
                );
            }
        }
        return resolved;
    }

    private static string ResolveHeaderValue(string name, string? value, IReadOnlyDictionary<string, string>? previous)
    {
        var resolved = value ?? previous?.GetValueOrDefault(name);
        if (resolved is null)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "An A2A credential can only keep a saved header value from the same origin."
            );
        }

        if (resolved.Length is 0 or > MaxHeaderValueLength || resolved.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"An A2A credential header value must contain 1 to {MaxHeaderValueLength} characters "
                    + "without line breaks."
            );
        }
        return resolved;
    }

    public static A2ASelectedInterface SelectInterface(
        IEnumerable<(string Url, string ProtocolBinding)> candidates,
        IReadOnlyList<string> supportedBindings
    )
    {
        foreach (var candidate in candidates)
        {
            if (ParseEndpointUri(candidate.Url) is null)
            {
                continue;
            }

            var binding = supportedBindings.FirstOrDefault(value =>
                string.Equals(candidate.ProtocolBinding, value, StringComparison.OrdinalIgnoreCase)
            );
            if (binding is not null)
            {
                return new(candidate.Url, binding);
            }
        }

        throw new AgwException(ErrorCodes.A2AAgentCardInvalid);
    }

    private static Uri? ParseEndpointUri(string? address) =>
        address is { Length: > 0 and <= MaxUrlLength }
        && Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri is { Scheme: "http" or "https", Host.Length: > 0, UserInfo.Length: 0 }
        && uri.IsWellFormedOriginalString()
            ? uri
            : null;

    private static bool IsTokenCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character);
}
