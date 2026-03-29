using Agw.Api.Contracts;
using Agw.Shared.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Api.Controllers;

public partial class AgentExecutionsController : ControllerBase
{
    [HttpPost("{id:guid}/execute")]
    public async Task<IActionResult> ExecuteAsync(
        Guid id,
        [FromBody] AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        return request.AgentType switch
        {
            AgentRuntimeType.Agent => await ExecuteAgentAsync(id, request, cancellationToken),
            AgentRuntimeType.Agentflow => await ExecuteAgentflowAsync(id, request, cancellationToken),
            _ => BadRequest("Invalid AgentType.")
        };
    }

    private async Task<IActionResult> ExecuteAgentAsync(
        Guid id,
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var (task, contextError) = await ResolveTaskAsync(request.TaskId, request.ProjectId);
        if (contextError != null)
        {
            return contextError;
        }

        var result = await _agentRuntimeService.ExecuteAsync(
            id,
            request.SessionId ?? string.Empty,
            request.Input,
            cancellationToken,
            request.ProjectId,
            task?.ContextId);
        if (result == null)
        {
            return NotFound();
        }

        return Ok(AgentExecutionResponse.FromAgentResult(result));
    }


    private async Task<IActionResult> ExecuteAgentflowAsync(
        Guid id,
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var (task, contextError) = await ResolveTaskAsync(request.TaskId, request.ProjectId);
        if (contextError != null)
        {
            return contextError;
        }

        var result = await _agentflowRuntimeService.ExecuteAsync(
            id,
            request.SessionId ?? string.Empty,
            request.Input,
            cancellationToken,
            request.ProjectId,
            task?.ContextId);
        if (result == null)
        {
            return NotFound();
        }

        return Ok(AgentExecutionResponse.FromAgentflowResult(result));
    }
}
