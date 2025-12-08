using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using DSystem.Manager.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using System.Linq.Expressions;

namespace DSystem.Manager.Api.Controllers;

[ApiController]
[Route("api/model-providers")]
public class ModelProvidersController : ControllerBase
{
    private readonly ModelProviderDomainService _service;

    public ModelProvidersController(ModelProviderDomainService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync([FromQuery] Guid? modelId = null, [FromQuery] Guid? providerId = null)
    {
        Expression<Func<ModelProvider, bool>>? predicate = null;
        if (modelId.HasValue || providerId.HasValue)
        {
            predicate = mp =>
                (!modelId.HasValue || mp.ModelId == modelId.Value) &&
                (!providerId.HasValue || mp.ProviderId == providerId.Value);
        }

        var links = await _service.ListAsync(predicate);
        return Ok(links);
    }

    [HttpGet("{modelId:guid}/{providerId:guid}")]
    public async Task<IActionResult> GetAsync(Guid modelId, Guid providerId)
    {
        var link = await _service.GetAsync(modelId, providerId);
        return link == null ? NotFound() : Ok(link);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] ModelProviderCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var link = new ModelProvider
        {
            ModelId = request.ModelId,
            ProviderId = request.ProviderId,
            InputPrice = request.InputPrice,
            OutputPrice = request.OutputPrice,
            CacheRead = request.CacheRead,
            CacheWrite = request.CacheWrite,
            RpsLimit = request.RpsLimit
        };

        var created = await _service.CreateAsync(link, user);
        return CreatedAtAction(nameof(GetAsync), new { modelId = created.ModelId, providerId = created.ProviderId }, created);
    }

    [HttpPut("{modelId:guid}/{providerId:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid modelId, Guid providerId, [FromBody] ModelProviderUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var updated = await _service.UpdateAsync(modelId, providerId, mp =>
        {
            mp.InputPrice = request.InputPrice;
            mp.OutputPrice = request.OutputPrice;
            mp.CacheRead = request.CacheRead;
            mp.CacheWrite = request.CacheWrite;
            mp.RpsLimit = request.RpsLimit;
        }, user);

        return updated == null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{modelId:guid}/{providerId:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid modelId, Guid providerId)
    {
        var deleted = await _service.DeleteAsync(modelId, providerId);
        return deleted ? NoContent() : NotFound();
    }
}
