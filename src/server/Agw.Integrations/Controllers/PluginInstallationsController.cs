using Agw.Integrations.Application.Management;
using Agw.Integrations.Contracts.Management;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Integrations.Controllers;

[ApiController]
[Route("api/integrations/plugin-installations")]
public sealed class PluginInstallationsController : ControllerBase
{
    private readonly PluginInstallationAppService _service;

    public PluginInstallationsController(PluginInstallationAppService service)
    {
        _service = service;
    }

    [HttpPut]
    [ProducesApiResult(typeof(PluginInstallationResponse))]
    public async Task<IActionResult> UpsertAsync(
        [FromBody] PluginInstallationUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var user = User?.Identity?.Name ?? "system";
        var response = await _service.UpsertAsync(request, user, cancellationToken);
        return AgwApiResult.Ok(response);
    }
}
