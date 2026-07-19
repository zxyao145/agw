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
[Route("api/providers")]
public class ProvidersController : ControllerBase
{
    private readonly IProviderAppService _service;
    private readonly IProviderModelDiscoveryService _modelDiscoveryService;

    public ProvidersController(
        IProviderAppService service,
        IProviderModelDiscoveryService modelDiscoveryService)
    {
        _service = service;
        _modelDiscoveryService = modelDiscoveryService;
    }

    [HttpGet]
    [ProducesApiResult(typeof(Provider[]))]
    public async Task<IActionResult> ListAsync()
    {
        var providers = await _service.ListAsync();
        return ApiResult.Ok(providers);
    }

    [HttpGet("{id:guid}")]
    [ProducesApiResult(typeof(Provider))]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var provider = await _service.GetAsync(id);
        return provider == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(provider);
    }

    [HttpPost]
    [ProducesApiResult(typeof(Provider))]
    public async Task<IActionResult> CreateAsync([FromBody] ProviderCreateRequest request)
    {
        var user = User?.Identity?.Name ?? Constants.AdminUserName;
        var created = await _service.CreateAsync(request, user);
        return ApiResult.Ok(created);
    }

    [HttpPost("discover-models")]
    [ProducesApiResult(typeof(ProviderModelDiscoveryResponse))]
    public async Task<IActionResult> DiscoverModelsAsync(
        [FromBody] ProviderModelDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _modelDiscoveryService.DiscoverAsync(request, cancellationToken);
        return ApiResult.Ok(result);
    }

    [HttpPut("{id:guid}")]
    [ProducesApiResult(typeof(Provider))]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ProviderUpdateRequest request)
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
