using System.Text;
using System.Text.Json;

using Agw.Setup.Contracts;
using Agw.Setup.Services;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Agw.Host.Controllers;

[ApiController]
[Route("api/setup")]
public class SetupApiController : ControllerBase
{
    private static readonly JsonSerializerOptions MobileLocalConfigJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IInitializationStateStore _stateStore;

    public SetupApiController(IInitializationStateStore stateStore)
    {
        _stateStore = stateStore;
    }

    [HttpPost("mobile-local-config")]
    public IActionResult GenerateMobileLocalConfig([FromBody] MobileLocalConfigRequest? request)
    {
        if (!TryNormalizeServerDomain(request?.ServerDomain, out var serverDomain))
        {
            return AgwApiResult.BadRequest("ServerDomain must be an absolute http or https URL without credentials, query, or fragment.");
        }

        var snapshot = _stateStore.GetSnapshot();
        if (!snapshot.IsInitialized)
        {
            return AgwApiResult.BadRequest("System is not initialized.");
        }

        var apiKey = snapshot.ApiKey?.Trim();
 

        var config = new MobileLocalConfigPayload(1, serverDomain, apiKey);
        var json = JsonSerializer.Serialize(config, MobileLocalConfigJsonOptions);
        var payload = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(json));

        return AgwApiResult.Ok(new MobileLocalConfigResponse(payload));
    }

    private static bool TryNormalizeServerDomain(string? value, out string serverDomain)
    {
        serverDomain = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        serverDomain = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return !string.IsNullOrWhiteSpace(serverDomain);
    }

    private sealed record MobileLocalConfigPayload(int Version, string ServerDomain, string ApiKey);

}
