using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Auth.Extensions;

public sealed class AgwOAuthOptions : OAuthOptions
{
    public string ProviderId { get; set; } = string.Empty;
    public string PublicCallbackUri { get; set; } = string.Empty;
    public OidcProviderOptions Provider { get; set; } = new();
}

public sealed class AgwOAuthHandler : OAuthHandler<AgwOAuthOptions>
{
    public AgwOAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AgwOAuthOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder
    )
        : base(options, logger, encoder) { }

    protected override string BuildChallengeUrl(AuthenticationProperties properties, string redirectUri) =>
        base.BuildChallengeUrl(properties, Options.PublicCallbackUri);

    protected override async Task<HandleRequestResult> HandleRemoteAuthenticateAsync()
    {
        Context.Items[OidcFlow.StartedKey] = Stopwatch.GetTimestamp();
        Context.Items[OidcDiagnostics.StageKey] = "protocol-validation";
        if (
            !HttpMethods.IsGet(Context.Request.Method)
            || !Context.RequestServices.GetRequiredService<IServerInitializationState>().IsInitialized
        )
            return HandleRequestResult.Fail("OAuth2 callback is not available.");

        return await base.HandleRemoteAuthenticateAsync();
    }

    protected override async Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeContext context)
    {
        try
        {
            var values = new Dictionary<string, string>
            {
                ["redirect_uri"] = Options.PublicCallbackUri,
                ["code"] = context.Code,
                ["grant_type"] = "authorization_code",
            };
            if (context.Properties.Items.TryGetValue(OAuthConstants.CodeVerifierKey, out var verifier))
            {
                if (verifier == null)
                    throw new AgwException(ErrorCodes.AuthenticationRequired);
                values[OAuthConstants.CodeVerifierKey] = verifier;
                context.Properties.Items.Remove(OAuthConstants.CodeVerifierKey);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, Options.TokenEndpoint);
            if (Options.Provider.ClientAuthMethod == OAuth2ClientAuthMethod.Post)
            {
                values["client_id"] = Options.ClientId;
                values["client_secret"] = Options.ClientSecret;
            }
            else
            {
                var credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(FormEncode(Options.ClientId) + ":" + FormEncode(Options.ClientSecret))
                );
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new FormUrlEncodedContent(values);
            using var response = await Backchannel.SendAsync(request, Context.RequestAborted);
            var body = await response.Content.ReadAsStringAsync(Context.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                OidcTelemetry.OAuth2TokenExchange(Options.ProviderId, "failure");
                return OAuthTokenResponse.Failed(
                    new AuthenticationException("OAuth2 token endpoint rejected the request.")
                );
            }

            try
            {
                var token = OAuthTokenResponse.Success(ParseTokenResponse(body));
                if (token.Error != null)
                {
                    token.Dispose();
                    OidcTelemetry.OAuth2TokenExchange(Options.ProviderId, "failure");
                    return OAuthTokenResponse.Failed(
                        new AuthenticationException("OAuth2 token endpoint returned an error.")
                    );
                }

                if (string.IsNullOrWhiteSpace(token.AccessToken))
                {
                    token.Dispose();
                    OidcTelemetry.OAuth2TokenExchange(Options.ProviderId, "failure");
                    return OAuthTokenResponse.Failed(
                        new AuthenticationException("OAuth2 token response did not contain an access token.")
                    );
                }

                OidcTelemetry.OAuth2TokenExchange(Options.ProviderId, "success");
                return token;
            }
            catch (JsonException)
            {
                OidcTelemetry.OAuth2TokenExchange(Options.ProviderId, "failure");
                return OAuthTokenResponse.Failed(new AuthenticationException("OAuth2 token response was invalid."));
            }
        }
        catch
        {
            OidcTelemetry.OAuth2TokenExchange(Options.ProviderId, "failure");
            throw;
        }
    }

    private static JsonDocument ParseTokenResponse(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
            return JsonDocument.Parse(body);

        var values = QueryHelpers
            .ParseQuery(body)
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
        return JsonSerializer.SerializeToDocument(values);
    }

    private static string FormEncode(string value) =>
        Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
}
