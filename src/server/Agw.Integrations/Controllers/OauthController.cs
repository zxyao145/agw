using System.Text.Encodings.Web;
using System.Text.Json;

using Agw.Integrations.Application.OAuth;
using Agw.Integrations.Contracts.OAuth;
using Agw.Shared;
using Agw.Shared.Results;

using Bens.Results;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Agw.Integrations.Controllers;

[ApiController]
[Route("api/integrations/oauth")]
public sealed class OAuthController : ControllerBase
{
    private const string DesktopCompletionPath = "/api/integrations/oauth/desktop-complete";
    private const string DesktopDeepLink = "agw-desktop://oauth/complete";
    private static readonly HashSet<string> DesktopErrorCodes =
    [
        "invalid_state",
        "authorization_denied",
        "token_exchange_failed"
    ];

    private readonly OAuthAuthorizationAppService _service;
    private readonly OAuthRefreshAppService _refreshService;
    private readonly OAuthRedirectUriResolver _redirectUriResolver;

    public OAuthController(
        OAuthAuthorizationAppService service,
        OAuthRefreshAppService refreshService,
        OAuthRedirectUriResolver redirectUriResolver)
    {
        _service = service;
        _refreshService = refreshService;
        _redirectUriResolver = redirectUriResolver;
    }

    [HttpGet("callback-info")]
    [ProducesApiResult(typeof(OAuthCallbackInfoResponse))]
    public IActionResult GetCallbackInfo()
    {
        return ApiResult.Ok(new OAuthCallbackInfoResponse
        {
            CallbackUrl = _redirectUriResolver.ResolveCallbackUri(BuildRequestBaseUri())
        });
    }

    [HttpPost("authorize-start")]
    [ProducesApiResult(typeof(OAuthAuthorizeStartResponse))]
    public async Task<IActionResult> AuthorizeStartAsync(
        [FromBody] OAuthAuthorizeStartRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _service.StartAsync(
            request.ConnectionId,
            _redirectUriResolver.ResolveCallbackUri(BuildRequestBaseUri()),
            request.ReturnPath,
            request.CompletionTarget,
            User?.Identity?.Name ?? Constants.AdminUserName,
            cancellationToken);
        return ApiResult.Ok(response);
    }

    [HttpGet("callback")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> CallbackAsync(CancellationToken cancellationToken)
    {
        var result = await _service.HandleCallbackAsync(
            Request.Query["state"].ToString(),
            Request.Query["code"].ToString(),
            Request.Query["error"].ToString(),
            User?.Identity?.Name ?? Constants.AdminUserName,
            cancellationToken);
        if (result.CompletionTarget == OAuthCompletionTarget.Desktop)
        {
            return Redirect(BuildDesktopCompletionPath(result.RedirectPath));
        }

        return Redirect(_redirectUriResolver.ResolveWebRedirectUri(
            BuildRequestBaseUri(),
            result.RedirectPath));
    }

    [HttpGet("desktop-complete")]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult DesktopComplete()
    {
        var oauth = string.Equals(
            Request.Query["oauth"].ToString(),
            "authorized",
            StringComparison.Ordinal)
            ? "authorized"
            : "error";
        var code = oauth == "error"
            ? NormalizeDesktopErrorCode(Request.Query["code"].ToString())
            : null;
        var parameters = new Dictionary<string, string?>
        {
            ["oauth"] = oauth,
            ["code"] = code
        };
        var deepLink = QueryHelpers.AddQueryString(DesktopDeepLink, parameters);
        var encodedDeepLink = HtmlEncoder.Default.Encode(deepLink);
        var serializedDeepLink = JsonSerializer.Serialize(deepLink);
        var title = oauth == "authorized" ? "Authorization complete" : "Authorization failed";
        var message = oauth == "authorized"
            ? "Agw Desktop has been authorized. Opening Integrations…"
            : "Agw Desktop could not complete authorization. Opening Integrations…";

        Response.Headers.CacheControl = "no-store";
        Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; base-uri 'none'; form-action 'none'";
        return Content($$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{title}}</title>
              <style>
                :root { color-scheme: light dark; font-family: ui-sans-serif, system-ui, sans-serif; }
                body { min-height: 100vh; margin: 0; display: grid; place-items: center; background: Canvas; color: CanvasText; }
                main { width: min(32rem, calc(100% - 3rem)); text-align: center; }
                p { color: GrayText; line-height: 1.6; }
                a { display: inline-block; margin-top: 1rem; padding: .7rem 1rem; border: 1px solid GrayText; border-radius: .5rem; color: inherit; text-decoration: none; }
              </style>
            </head>
            <body>
              <main>
                <h1>{{title}}</h1>
                <p>{{message}}</p>
                <p>If Agw Desktop does not open automatically, return to the app or use the button below.</p>
                <a href="{{encodedDeepLink}}">Open Agw Desktop Integrations</a>
              </main>
              <script>window.location.replace({{serializedDeepLink}});</script>
            </body>
            </html>
            """, "text/html; charset=utf-8");
    }

    [HttpPost("refresh")]
    [ProducesApiResult(typeof(OAuthRefreshResponse))]
    public async Task<IActionResult> RefreshAsync(
        [FromBody] OAuthRefreshRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _refreshService.RefreshAsync(
            request.ConnectionId,
            User?.Identity?.Name ?? Constants.AdminUserName,
            cancellationToken);
        return ApiResult.Ok(response);
    }

    private string BuildRequestBaseUri()
    {
        return UriHelper.BuildAbsolute(
            Request.Scheme,
            Request.Host,
            Request.PathBase,
            "/");
    }

    private static string BuildDesktopCompletionPath(string redirectPath)
    {
        var queryIndex = redirectPath.IndexOf('?', StringComparison.Ordinal);
        var query = queryIndex >= 0
            ? QueryHelpers.ParseQuery(redirectPath[queryIndex..])
            : new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>();
        var oauth = string.Equals(query["oauth"].ToString(), "authorized", StringComparison.Ordinal)
            ? "authorized"
            : "error";
        var code = oauth == "error"
            ? NormalizeDesktopErrorCode(query["code"].ToString())
            : null;
        return QueryHelpers.AddQueryString(
            DesktopCompletionPath,
            new Dictionary<string, string?>
            {
                ["oauth"] = oauth,
                ["code"] = code
            });
    }

    private static string NormalizeDesktopErrorCode(string code)
    {
        return DesktopErrorCodes.Contains(code) ? code : "invalid_state";
    }
}
