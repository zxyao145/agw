using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using DSystem.Manager.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using System.Linq.Expressions;

namespace DSystem.Manager.Api.Controllers;

[ApiController]
[Route("api/model-provider-keys")]
public class ModelProviderApiKeysController : ControllerBase
{
    private readonly ModelProviderApiKeyDomainService _service;

    public ModelProviderApiKeysController(ModelProviderApiKeyDomainService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync([FromQuery] Guid? modelProviderId = null, [FromQuery] bool? enable = null)
    {
        Expression<Func<ModelProviderApiKey, bool>>? predicate = null;
        if (modelProviderId.HasValue || enable.HasValue)
        {
            predicate = apiKey =>
                (!modelProviderId.HasValue || apiKey.ModelProviderId == modelProviderId.Value) &&
                (!enable.HasValue || apiKey.Enable == enable.Value);
        }

        var keys = await _service.ListDtoAsync(predicate);
        return Ok(keys);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var key = await _service.GetAsync(id);
        return key == null ? NotFound() : Ok(key);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] ApiKeyCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var apiKey = new ModelProviderApiKey
        {
            ModelProviderId = request.ModelProviderId,
            ApiKey = request.ApiKey,
            Enable = request.Enable
        };

        var created = await _service.CreateAsync(apiKey, user);
        return CreatedAtAction(nameof(GetAsync), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ApiKeyUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var updated = await _service.UpdateAsync(id, key =>
        {
            key.ApiKey = request.ApiKey;
            key.Enable = request.Enable;
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
