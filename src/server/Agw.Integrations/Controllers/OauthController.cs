using Agw.Integrations.Application.OAuth;
using Agw.Integrations.Contracts.OAuth;
using Agw.Shared;
using Agw.Shared.Results;

using Bens.Results;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Integrations.Controllers;

[ApiController]
[Route("api/integrations/oauth")]
public sealed class OAuthController : ControllerBase
{
    private const string CallbackPath = "/api/integrations/oauth/callback";

    private readonly OAuthAuthorizationAppService _service;
    private readonly OAuthRefreshAppService _refreshService;

    public OAuthController(
        OAuthAuthorizationAppService service,
        OAuthRefreshAppService refreshService)
    {
        _service = service;
        _refreshService = refreshService;
    }

    [HttpPost("authorize-start")]
    [ProducesApiResult(typeof(OAuthAuthorizeStartResponse))]
    public async Task<IActionResult> AuthorizeStartAsync(
        [FromBody] OAuthAuthorizeStartRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _service.StartAsync(
            request.ConnectionId,
            BuildCallbackUri(),
            request.ReturnPath,
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
            BuildCallbackUri(),
            User?.Identity?.Name ?? Constants.AdminUserName,
            cancellationToken);
        return Redirect(result.RedirectPath);
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

    private string BuildCallbackUri()
    {
        return UriHelper.BuildAbsolute(
            Request.Scheme,
            Request.Host,
            Request.PathBase,
            CallbackPath);
    }
}
