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

    [HttpGet("{id:guid}/nodes")]
    public async Task<IActionResult> ListNodesAsync(Guid id)
    {
        var workflow = await _workflowService.GetAsync(id);
        if (workflow == null)
        {
            return NotFound();
        }

        var nodes = await _workflowService.ListNodesAsync(id);
        return Ok(nodes);
    }

    [HttpGet("{id:guid}/edges")]
    public async Task<IActionResult> ListEdgesAsync(Guid id)
    {
        var workflow = await _workflowService.GetAsync(id);
        if (workflow == null)
        {
            return NotFound();
        }

        var edges = await _workflowService.ListEdgesAsync(id);
        return Ok(edges);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] WorkflowCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";

        var workflow = new Workflow
        {
            Name = request.Name,
            Description = request.Description,
            SystemPrompt = request.SystemPrompt,
            Pattern = request.Pattern,
            ConfigurationJson = request.ConfigurationJson,
            Enable = request.Enable
        };

        var nodes = request.Nodes
            .Select(x => new WorkflowNode
            {
                NodeId = x.NodeId,
                Type = x.Type,
                RelateId = x.RelateId,
            })
            .ToList();

        var edges = request.Edges
            .Select(x => new WorkflowEdge
            {
                EdgeId = x.EdgeId,
                SourceNodeId = x.SourceNodeId,
                TargetNodeId = x.TargetNodeId,
                Animated = x.Animated,
            })
            .ToList();

        var created = await _workflowService.CreateAsync(workflow, nodes, edges, user);
        if (created == null)
        {
            return BadRequest("Failed to create workflow (validation failed or referenced resources not found).");
        }

        return CreatedAtAction(nameof(GetAsync), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] WorkflowUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";

        var nodes = request.Nodes
            .Select(x => new WorkflowNode
            {
                NodeId = x.NodeId,
                Type = x.Type,
                RelateId = x.RelateId,
            })
            .ToList();

        var edges = request.Edges
            .Select(x => new WorkflowEdge
            {
                EdgeId = x.EdgeId,
                SourceNodeId = x.SourceNodeId,
                TargetNodeId = x.TargetNodeId,
                Animated = x.Animated,
            })
            .ToList();

        var updated = await _workflowService.UpdateAsync(id, workflow =>
        {
            workflow.Name = request.Name;
            workflow.Description = request.Description;
            workflow.SystemPrompt = request.SystemPrompt;
            workflow.Pattern = request.Pattern;
            workflow.ConfigurationJson = request.ConfigurationJson;
            workflow.Enable = request.Enable;
        }, nodes, edges, user);

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