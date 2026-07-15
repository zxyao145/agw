using Agw.Integrations.Application.Management;
using Agw.Integrations.Contracts.Management;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Integrations.Controllers;

[ApiController]
[Route("api/integrations/connections")]
public sealed class ConnectionsController : ControllerBase
{
    private readonly ConnectionAppService _service;

    public ConnectionsController(ConnectionAppService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesApiResult(typeof(ConnectionResponse[]))]
    public async Task<IActionResult> ListAsync(
        [FromQuery] Guid? id,
        CancellationToken cancellationToken)
    {
        var response = await _service.ListAsync(id, cancellationToken);
        return id.HasValue && response.Count == 0
            ? AgwApiResult.FromError(ErrorCodes.ConnectionNotFound)
            : AgwApiResult.Ok(response);
    }

    [HttpPost]
    [ProducesApiResult(typeof(ConnectionResponse))]
    public async Task<IActionResult> CreateAsync(
        [FromBody] ConnectionCreateRequest request,
        CancellationToken cancellationToken)
    {
        var user = User?.Identity?.Name ?? "system";
        var response = await _service.CreateAsync(request, user, cancellationToken);
        return AgwApiResult.Ok(response);
    }

    [HttpPut]
    [ProducesApiResult(typeof(ConnectionResponse))]
    public async Task<IActionResult> UpdateAsync(
        [FromBody] ConnectionUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var user = User?.Identity?.Name ?? "system";
        var response = await _service.UpdateAsync(request, user, cancellationToken);
        return AgwApiResult.Ok(response);
    }

    [HttpDelete]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(
        [FromQuery] Guid id,
        CancellationToken cancellationToken)
    {
        return await _service.DeleteAsync(id, cancellationToken)
            ? AgwApiResult.Ok()
            : AgwApiResult.FromError(ErrorCodes.ConnectionNotFound);
    }

    [HttpPost("validate")]
    [ProducesApiResult(typeof(ConnectionResponse))]
    public async Task<IActionResult> ValidateAsync(
        [FromBody] ConnectionValidateRequest request,
        CancellationToken cancellationToken)
    {
        var user = User?.Identity?.Name ?? "system";
        var response = await _service.ValidateAsync(request.Id, user, cancellationToken);
        return AgwApiResult.Ok(response);
    }
}
