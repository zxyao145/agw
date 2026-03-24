using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Agw.Api.Controllers;

[ApiController]
[Route("api/integrations/oauth")]
public class IntegrationsController : ControllerBase
{
    [HttpGet("callback")]
    public IActionResult OAuthCallback()
    {
        var callbackPath = "/integrations/callback";
        var queryParameters = new List<KeyValuePair<string, string?>>();

        foreach (var (key, values) in Request.Query)
        {
            foreach (var value in values)
            {
                queryParameters.Add(new KeyValuePair<string, string?>(key, value));
            }
        }

        var redirectUrl = queryParameters.Count == 0
            ? callbackPath
            : QueryHelpers.AddQueryString(callbackPath, queryParameters);

        return Redirect(redirectUrl);
    }
}
