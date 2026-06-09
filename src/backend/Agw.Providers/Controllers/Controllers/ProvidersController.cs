using Agw.Providers.Application;
using Agw.Providers.Contracts.Manager;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Providers.Controllers.Controllers;

[ApiController]
[Route("api/providers")]
public class ProvidersController : ControllerBase
{
    private readonly IProviderAppService _service;

    public ProvidersController(IProviderAppService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesApiResult(typeof(Provider[]))]
    public async Task<IActionResult> ListAsync()
    {
        var providers = await _service.ListAsync();
        return AgwApiResult.Ok(providers);
    }

    [HttpGet("{id:guid}")]
    [ProducesApiResult(typeof(Provider))]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var provider = await _service.GetAsync(id);
        return provider == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(provider);
    }

    [HttpPost]
    [ProducesApiResult(typeof(Provider))]
    public async Task<IActionResult> CreateAsync([FromBody] ProviderCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var created = await _service.CreateAsync(request, user);
        return AgwApiResult.Ok(created);
    }

    [HttpPut("{id:guid}")]
    [ProducesApiResult(typeof(Provider))]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ProviderUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var updated = await _service.UpdateAsync(id, request, user);

        return updated == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _service.DeleteAsync(id);
        return deleted ? AgwApiResult.Ok() : AgwApiResult.NotFound();
    }
}
