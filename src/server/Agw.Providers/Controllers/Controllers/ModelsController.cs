using Agw.Providers.Application;
using Agw.Providers.Contracts.Manager;
using Agw.Shared;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Bens.Results;
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
    [ProducesApiResult(typeof(AgwAiModel[]))]
    public async Task<IActionResult> ListAsync()
    {
        var models = await _service.ListAsync();
        return ApiResult.Ok(models);
    }

    [HttpGet("{id:guid}")]
    [ProducesApiResult(typeof(AgwAiModel))]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var model = await _service.GetAsync(id);
        return model == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(model);
    }

    [HttpPost]
    [ProducesApiResult(typeof(AgwAiModel))]
    public async Task<IActionResult> CreateAsync([FromBody] ModelCreateRequest request)
    {
        var user = User?.Identity?.Name ?? Constants.AdminUserName;
        var created = await _service.CreateAsync(request, user);
        return ApiResult.Ok(created);
    }

    [HttpPut("{id:guid}")]
    [ProducesApiResult(typeof(AgwAiModel))]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ModelUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? Constants.AdminUserName;
        var updated = await _service.UpdateAsync(id, request, user);

        return updated == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _service.DeleteAsync(id);
        return deleted ? ApiResult.Ok() : ErrorCodes.ResourceNotFound.ToApiResult();
    }
}
