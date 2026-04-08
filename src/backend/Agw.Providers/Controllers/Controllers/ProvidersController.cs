using Agw.Providers.Application;
using Agw.Providers.Contracts.Manager;
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
    public async Task<IActionResult> ListAsync()
    {
        var providers = await _service.ListAsync();
        return Ok(providers);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var provider = await _service.GetAsync(id);
        return provider == null ? NotFound() : Ok(provider);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] ProviderCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var created = await _service.CreateAsync(request, user);
        return Ok(created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ProviderUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var updated = await _service.UpdateAsync(id, request, user);

        return updated == null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _service.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}
