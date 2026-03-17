using Agw.Domain.Entities;
using Agw.Domain.Services;
using Agw.Manager.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Manager.Api.Controllers;

[ApiController]
[Route("api/models")]
public class ModelsController : ControllerBase
{
    private readonly ModelDomainService _service;

    public ModelsController(ModelDomainService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync()
    {
        var models = await _service.ListAsync();
        return Ok(models);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var model = await _service.GetAsync(id);
        return model == null ? NotFound() : Ok(model);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] ModelCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var model = new LlmModel
        {
            Name = request.Name,
            Description = request.Description,
            Type = request.Type,
            MaxTokens = request.MaxTokens
        };

        var created = await _service.CreateAsync(model, user);
        return Ok(created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ModelUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var updated = await _service.UpdateAsync(id, m =>
        {
            m.Name = request.Name;
            m.Description = request.Description;
            m.Type = request.Type;
            m.MaxTokens = request.MaxTokens;
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
