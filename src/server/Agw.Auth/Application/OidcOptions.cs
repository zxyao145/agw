using System.Text.RegularExpressions;
using Agw.Auth.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Agw.Auth.Application;

public sealed partial class OidcOptions : IOidcProviderAvailability
{
    public string PublicBaseUrl { get; set; } = string.Empty;

    // Browser-facing origin for the login/dashboard redirects. Empty means "same origin as
    // PublicBaseUrl" (single-origin production). Set it in split dev topologies where the SPA is
    // served from a different origin than the OAuth callback (e.g. web :3001, backend :30816).
    public string WebBaseUrl { get; set; } = string.Empty;
    public Dictionary<string, OidcProviderOptions> Providers { get; set; } = new(StringComparer.Ordinal);

    public bool IsEnabled(string providerId) => Providers.TryGetValue(providerId, out var provider) && provider.Enabled;

    public OidcProviderOptions RequireProvider(string? id)
    {
        if (id == null || !Providers.TryGetValue(id, out var provider) || !provider.Enabled)
            throw new AgwException(ErrorCodes.InvalidParam, "OIDC provider is not available.");
        return provider;
    }

    public OidcProviderResponse[] ListProviders() =>
        Providers
            .Where(pair => pair.Value.Enabled)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new OidcProviderResponse(pair.Key, pair.Value.DisplayName ?? pair.Key, pair.Value.Type))
            .ToArray();

    public static OidcOptions Load(IConfiguration configuration, IHostEnvironment environment)
    {
        var errors = new List<string>();
        ValidateEnumValues(configuration.GetSection("Auth:Oidc:Providers"), errors);
        if (errors.Count > 0)
            throw new AgwException(ErrorCodes.InvalidParam, string.Join(" ", errors));

        OidcOptions options;
        try
        {
            options = configuration.GetSection("Auth:Oidc").Get<OidcOptions>() ?? new OidcOptions();
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or FormatException)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Auth:Oidc contains an invalid configuration value.");
        }

        options.PublicBaseUrl = options.PublicBaseUrl.TrimEnd('/');
        if (
            options.Providers.Any(pair => pair.Value.Enabled)
            && !ValidUrl(options.PublicBaseUrl, environment, rootOnly: true)
        )
            errors.Add("Auth:Oidc:PublicBaseUrl must be an HTTPS origin (HTTP loopback is allowed in Development).");
        options.WebBaseUrl = options.WebBaseUrl.TrimEnd('/');
        if (
            options.Providers.Any(pair => pair.Value.Enabled)
            && !string.IsNullOrEmpty(options.WebBaseUrl)
            && !ValidUrl(options.WebBaseUrl, environment, rootOnly: true)
        )
            errors.Add("Auth:Oidc:WebBaseUrl must be an HTTPS origin (HTTP loopback is allowed in Development).");
        foreach (var (id, provider) in options.Providers)
        {
            if (id.Length is < 1 or > 64 || !ProviderIdRegex().IsMatch(id))
                errors.Add("OIDC provider IDs must use lowercase kebab-case and at most 64 characters.");
            if (!provider.Enabled)
                continue;
            if (string.IsNullOrWhiteSpace(provider.ClientId) || string.IsNullOrWhiteSpace(provider.ClientSecret))
                errors.Add($"Provider '{id}' requires ClientId and ClientSecret.");
            if (provider.Type == AuthProviderType.Oidc)
            {
                if (!ValidProviderUrl(provider.Authority, environment))
                    errors.Add($"OIDC provider '{id}' requires a valid Authority.");
            }
            else
            {
                if (!ValidProviderUrl(provider.AuthorizationEndpoint, environment))
                    errors.Add($"OAuth2 provider '{id}' requires a valid AuthorizationEndpoint.");
                if (!ValidProviderUrl(provider.TokenEndpoint, environment))
                    errors.Add($"OAuth2 provider '{id}' requires a valid TokenEndpoint.");
                if (!ValidProviderUrl(provider.Issuer, environment))
                    errors.Add($"OAuth2 provider '{id}' requires a valid Issuer.");
                if (provider.IdentitySource == OAuth2IdentitySource.UserInfo)
                {
                    if (!ValidProviderUrl(provider.UserInfoEndpoint, environment))
                        errors.Add($"OAuth2 provider '{id}' requires a valid UserInfoEndpoint.");
                }
                else if (
                    !ValidProviderUrl(provider.AccessTokenJwksUri, environment)
                    || !ValidProviderUrl(provider.AccessTokenIssuer, environment)
                    || string.IsNullOrWhiteSpace(provider.AccessTokenAudience)
                )
                {
                    errors.Add(
                        $"OAuth2 provider '{id}' requires AccessTokenIssuer, AccessTokenAudience and AccessTokenJwksUri."
                    );
                }
            }
            if (string.IsNullOrWhiteSpace(provider.DisplayName))
                provider.DisplayName = id;
        }
        if (errors.Count > 0)
            throw new AgwException(ErrorCodes.InvalidParam, string.Join(" ", errors));
        return options;
    }

    private static void ValidateEnumValues(IConfigurationSection providers, List<string> errors)
    {
        foreach (var provider in providers.GetChildren())
        {
            ValidateEnum<AuthProviderType>(provider, "Type", errors);
            ValidateEnum<OAuth2ClientAuthMethod>(provider, "ClientAuthMethod", errors);
            ValidateEnum<OAuth2IdentitySource>(provider, "IdentitySource", errors);
        }
    }

    private static void ValidateEnum<T>(IConfigurationSection provider, string key, List<string> errors)
        where T : struct, Enum
    {
        var value = provider[key];
        if (
            string.IsNullOrWhiteSpace(value)
            || Enum.GetNames<T>().Any(name => string.Equals(name, value.Trim(), StringComparison.OrdinalIgnoreCase))
        )
            return;

        errors.Add($"Provider '{provider.Key}' has an invalid {key}; use a named string enum value.");
    }

    private static bool ValidProviderUrl(string value, IHostEnvironment environment) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && ValidUrl(value, environment, rootOnly: false);

    private static bool ValidUrl(string value, IHostEnvironment environment, bool rootOnly) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && (!rootOnly || uri.AbsolutePath == "/")
        && (
            uri.Scheme == Uri.UriSchemeHttps
            || environment.IsDevelopment() && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback
        );

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderIdRegex();
}

public sealed class OidcProviderOptions
{
    public bool Enabled { get; set; }
    public AuthProviderType Type { get; set; } = AuthProviderType.Oidc;
    public string? DisplayName { get; set; }
    public string Authority { get; set; } = string.Empty;
    public string AuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public string UserInfoEndpoint { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public OAuth2IdentitySource IdentitySource { get; set; } = OAuth2IdentitySource.UserInfo;
    public OAuth2ClientAuthMethod ClientAuthMethod { get; set; } = OAuth2ClientAuthMethod.Post;
    public bool UsePkce { get; set; }
    public string[] Scopes { get; set; } = [];
    public string SubjectClaim { get; set; } = "sub";
    public string DisplayNameClaim { get; set; } = "name";
    public string EmailClaim { get; set; } = "email";
    public string AccessTokenIssuer { get; set; } = string.Empty;
    public string AccessTokenAudience { get; set; } = string.Empty;
    public string AccessTokenJwksUri { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}
