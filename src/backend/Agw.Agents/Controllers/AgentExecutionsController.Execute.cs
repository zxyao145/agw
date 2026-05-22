using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Contracts;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Api.Controllers;

public partial class AgentExecutionsController : ControllerBase
{
    [HttpPost("{id:guid}/execute")]
    [ProducesApiResult(typeof(AgentExecutionResponse))]
    public async Task<IActionResult> ExecuteAsync(
        Guid id,
        [FromBody] AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        return request.AgentType switch
        {
            AgentRuntimeType.Agent => await ExecuteAgentAsync(id, request, cancellationToken),
            AgentRuntimeType.Agentflow => await ExecuteAgentflowAsync(id, request, cancellationToken),
            _ => AgwApiResult.BadRequest("Invalid AgentType.")
        };
    }

    private async Task<IActionResult> ExecuteAgentAsync(
        Guid id,
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var taskResolution = await _taskAppService.ResolveTaskAsync(
            new ExecutionTaskRequest(
                TaskId: request.TaskId,
                ProjectId: request.ProjectId,
                Input: request.Input,
                Resume: false,
                User: User?.Identity?.Name ?? "system"),
            cancellationToken);
        var task = taskResolution.Task;
        var contextError = taskResolution.Error;
        if (contextError != null)
        {
            return contextError;
        }

        var req = new AgentExecuteByIdRequest(request.Input, id, request.TaskId, request.ProjectId, "");

        var result = await _agentRuntimeService.ExecuteByIdAsync(req, cancellationToken);
        if (result == null)
        {
            return AgwApiResult.NotFound();
        }

        return AgwApiResult.Ok(AgentExecutionResponse.FromAgentResult(result));
    }

    private async Task<IActionResult> ExecuteAgentflowAsync(
        Guid id,
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var taskResolution = await _taskAppService.ResolveTaskAsync(
            new ExecutionTaskRequest(
                TaskId: request.TaskId,
                ProjectId: request.ProjectId,
                Input: request.Input,
                Resume: false,
                User: User?.Identity?.Name ?? "system"),
            cancellationToken);
        var task = taskResolution.Task;
        var contextError = taskResolution.Error;
        if (contextError != null)
        {
            return contextError;
        }

        var result = await _agentflowRuntimeService.ExecuteAsync(
             id,
             request.TaskId!.Value,
             request.Input,
             cancellationToken,
             request.ProjectId,
             "");
        if (result == null)
        {
            return AgwApiResult.NotFound();
        }

        return AgwApiResult.Ok(AgentExecutionResponse.FromAgentflowResult(result));
    }
}
