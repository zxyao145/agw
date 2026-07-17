using Agw.Integrations.Application.Management;
using Agw.Integrations.Contracts.Management;
using Agw.Shared.Results;

using Bens.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Integrations.Controllers;

[ApiController]
[Route("api/integrations/plugins")]
public sealed class PluginsController : ControllerBase
{
    private readonly PluginCatalogAppService _service;

    public PluginsController(PluginCatalogAppService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesApiResult(typeof(PluginResponse[]))]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return ApiResult.Ok(await _service.ListAsync(cancellationToken));
    }
}
