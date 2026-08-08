using Agw.Shared.Exceptions;

using Microsoft.Extensions.Options;

namespace Agw.Integrations.Application.OAuth;

public sealed class OAuthRedirectOptions
{
    public const string SectionName = "Integrations:OAuth";

    public string? PublicBaseUrl { get; set; }
    public string? WebBaseUrl { get; set; }
}

public sealed class OAuthRedirectUriResolver
{
    public const string CallbackPath = "/api/integrations/oauth/callback";

    private readonly OAuthRedirectOptions _options;

    public OAuthRedirectUriResolver(IOptions<OAuthRedirectOptions> options)
    {
        _options = options.Value;
    }

    public string ResolveCallbackUri(string requestBaseUri)
    {
        return Combine(_options.PublicBaseUrl, requestBaseUri, CallbackPath);
    }

    public string ResolveWebRedirectUri(string requestBaseUri, string redirectPath)
    {
        OAuthStateProtector.ValidateReturnPath(redirectPath);
        return Combine(_options.WebBaseUrl, requestBaseUri, redirectPath);
    }

    public static bool IsValidOptionalBaseUrl(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || TryValidateBaseUrl(value, out _);
    }

    private static string Combine(string? configuredBaseUrl, string fallbackBaseUri, string path)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? fallbackBaseUri
            : configuredBaseUrl;
        if (!TryValidateBaseUrl(baseUrl, out var baseUri))
        {
            throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
        }

        return new Uri(baseUri, path.TrimStart('/')).ToString();
    }

    private static bool TryValidateBaseUrl(string value, out Uri uri)
    {
        if (!Uri.TryCreate(EnsureTrailingSlash(value.Trim()), UriKind.Absolute, out uri!)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            uri = null!;
            return false;
        }

        return true;
    }

    private static string EnsureTrailingSlash(string value)
    {
        return value.EndsWith('/') ? value : $"{value}/";
    }
}
