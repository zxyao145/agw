using Agw.Providers.Application;
using Agw.Providers.Contracts.Manager;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Providers.Controllers.Controllers;

[ApiController]
[Route("api/model-providers")]
public class ModelProvidersController : ControllerBase
{
    private readonly IModelProviderAppService _service;

    public ModelProvidersController(IModelProviderAppService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync([FromQuery] Guid? modelId = null, [FromQuery] Guid? providerId = null)
    {
        var entities = await _service.ListAsync(modelId, providerId);
        var result = entities.Select(mp => new
        {
            mp.Id,
            mp.ModelId,
            mp.ProviderId,
            ModelName = mp.Model?.Name ?? string.Empty,
            ProviderName = mp.Provider?.Name ?? string.Empty,
            mp.InputPrice,
            mp.OutputPrice,
            mp.CacheRead,
            mp.CacheWrite,
            mp.RpsLimit,
            mp.CreateTime,
            mp.CreateBy,
            mp.UpdateTime,
            mp.UpdateBy
        }).ToList();
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var entity = await _service.GetAsync(id);
        return entity == null ? NotFound() : Ok(entity);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] ModelProviderCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var created = await _service.CreateAsync(request, user);
        return Ok(created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ModelProviderUpdateRequest request)
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
