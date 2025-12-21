using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using DSystem.Manager.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace DSystem.Manager.Api.Controllers;

[ApiController]
[Route("api/workflows")]
public class WorkflowsController : ControllerBase
{
    private readonly WorkflowDomainService _workflowService;
    private readonly WorkflowRuntimeService _workflowRuntimeService;

    public WorkflowsController(
        WorkflowDomainService workflowService,
        WorkflowRuntimeService workflowRuntimeService)
    {
        _workflowService = workflowService;
        _workflowRuntimeService = workflowRuntimeService;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync()
    {
        var workflows = await _workflowService.ListAsync();
        return Ok(workflows);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var workflow = await _workflowService.GetAsync(id);
        return workflow == null ? NotFound() : Ok(workflow);
    }

    [HttpGet("{id:guid}/agents")]
    public async Task<IActionResult> ListAgentsAsync(Guid id)
    {
        var workflow = await _workflowService.GetAsync(id);
        if (workflow == null)
        {
            return NotFound();
        }

        var agents = await _workflowService.ListAgentsAsync(id);
        return Ok(agents);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] WorkflowCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";

        var workflow = new Workflow
        {
            Name = request.Name,
            Description = request.Description,
            Pattern = request.Pattern,
            ConfigurationJson = request.ConfigurationJson,
            Enable = request.Enable
        };

        var agents = request.Agents
            .Select(x => new WorkflowNode
            {
                RelateId = x.AgentId,
            })
            .ToList();

        var created = await _workflowService.CreateAsync(workflow, agents, user);
        if (created == null)
        {
            return BadRequest("Failed to create workflow (validation or referenced agents not found).");
        }

        return CreatedAtAction(nameof(GetAsync), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] WorkflowUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";

        var agents = request.Agents
            .Select(x => new WorkflowNode
            {
                RelateId = x.AgentId,
            })
            .ToList();

        var updated = await _workflowService.UpdateAsync(id, workflow =>
        {
            workflow.Name = request.Name;
            workflow.Description = request.Description;
            workflow.Pattern = request.Pattern;
            workflow.ConfigurationJson = request.ConfigurationJson;
            workflow.Enable = request.Enable;
        }, agents, user);

        return updated == null
            ? NotFound()
            : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _workflowService.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/execute")]
    public async Task<IActionResult> ExecuteAsync(Guid id, [FromBody] WorkflowExecuteRequest request, CancellationToken cancellationToken)
    {
        var result = await _workflowRuntimeService.ExecuteAsync(id, request.Input, cancellationToken);
        if (result == null)
        {
            return NotFound();
        }

        return Ok(WorkflowExecuteResponse.FromDomain(result));
    }
}