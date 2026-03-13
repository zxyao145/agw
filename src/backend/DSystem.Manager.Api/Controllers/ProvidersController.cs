using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using DSystem.Manager.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace DSystem.Manager.Api.Controllers;

[ApiController]
[Route("api/providers")]
public class ProvidersController : ControllerBase
{
    private readonly ProviderDomainService _service;

    public ProvidersController(ProviderDomainService service)
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
        var provider = new Provider
        {
            Name = request.Name,
            ProviderType = request.ProviderType,
            Description = request.Description,
            Endpoint = request.Endpoint
        };

        var created = await _service.CreateAsync(provider, user);
        return Ok(created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ProviderUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var updated = await _service.UpdateAsync(id, p =>
        {
            p.Name = request.Name;
            p.ProviderType = request.ProviderType;
            p.Description = request.Description;
            p.Endpoint = request.Endpoint;
        }, user);

        return updated == null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _service.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}
