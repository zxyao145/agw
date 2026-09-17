using System.Text.RegularExpressions;
using Agw.Auth.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Agw.Auth.Application;

public sealed class OidcOptions : IOidcProviderAvailability
{
    public string PublicBaseUrl { get; set; } = string.Empty;
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
            .Select(pair => new OidcProviderResponse(pair.Key, pair.Value.DisplayName ?? pair.Key))
            .ToArray();

    public static OidcOptions Load(IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection("Auth:Oidc").Get<OidcOptions>() ?? new OidcOptions();
        options.PublicBaseUrl = options.PublicBaseUrl.TrimEnd('/');
        var errors = new List<string>();
        if (
            options.Providers.Any(pair => pair.Value.Enabled)
            && !ValidUrl(options.PublicBaseUrl, environment, rootOnly: true)
        )
            errors.Add("Auth:Oidc:PublicBaseUrl must be an HTTPS origin (HTTP loopback is allowed in Development).");
        foreach (var (id, provider) in options.Providers)
        {
            if (
                id.Length is < 1 or > 64
                || !Regex.IsMatch(id, "^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)
            )
                errors.Add("OIDC provider IDs must use lowercase kebab-case and at most 64 characters.");
            if (!provider.Enabled)
                continue;
            if (
                !ValidUrl(provider.Authority, environment, rootOnly: false)
                || provider.Authority.Length > 512
                || string.IsNullOrWhiteSpace(provider.ClientId)
                || string.IsNullOrWhiteSpace(provider.ClientSecret)
            )
                errors.Add($"OIDC provider '{id}' requires a valid Authority, ClientId and ClientSecret.");
            if (string.IsNullOrWhiteSpace(provider.DisplayName))
                provider.DisplayName = id;
        }
        if (errors.Count > 0)
            throw new AgwException(ErrorCodes.InvalidParam, string.Join(" ", errors));
        return options;
    }

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
}

public sealed class OidcProviderOptions
{
    public bool Enabled { get; set; }
    public string? DisplayName { get; set; }
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}
