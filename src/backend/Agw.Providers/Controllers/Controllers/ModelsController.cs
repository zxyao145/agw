using Agw.Providers.Application;
using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Entities;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Providers.Controllers.Controllers;

[ApiController]
[Route("api/models")]
public class ModelsController : ControllerBase
{
    private readonly IModelAppService _service;

    public ModelsController(IModelAppService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesApiResult(typeof(LlmModel[]))]
    public async Task<IActionResult> ListAsync()
    {
        var models = await _service.ListAsync();
        return AgwApiResult.Ok(models);
    }

    [HttpGet("{id:guid}")]
    [ProducesApiResult(typeof(LlmModel))]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var model = await _service.GetAsync(id);
        return model == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(model);
    }

    [HttpPost]
    [ProducesApiResult(typeof(LlmModel))]
    public async Task<IActionResult> CreateAsync([FromBody] ModelCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var created = await _service.CreateAsync(request, user);
        return AgwApiResult.Ok(created);
    }

    [HttpPut("{id:guid}")]
    [ProducesApiResult(typeof(LlmModel))]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ModelUpdateRequest request)
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
