using Agw.Domain.Entities;
using Agw.Domain.Services;
using Agw.Manager.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using System.Linq.Expressions;

namespace Agw.Manager.Api.Controllers;

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

        var entities = await _service.ListWithDetailsAsync(predicate);
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
        var entity = new ModelProvider
        {
            ModelId = request.ModelId,
            ProviderId = request.ProviderId,
            InputPrice = request.InputPrice,
            OutputPrice = request.OutputPrice,
            CacheRead = request.CacheRead,
            CacheWrite = request.CacheWrite,
            RpsLimit = request.RpsLimit
        };

        var created = await _service.CreateAsync(entity, user);
        return Ok(created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ModelProviderUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var updated = await _service.UpdateAsync(id, mp =>
        {
            mp.InputPrice = request.InputPrice;
            mp.OutputPrice = request.OutputPrice;
            mp.CacheRead = request.CacheRead;
            mp.CacheWrite = request.CacheWrite;
            mp.RpsLimit = request.RpsLimit;
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
