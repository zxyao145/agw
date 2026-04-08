using Agw.Agents.Domain.Entities;
using Agw.Appliaction.Services.Agentflows;
using Agw.Manager.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Manager.Api.Controllers;

[ApiController]
[Route("api/agentflows")]
public class AgentflowsController : ControllerBase
{
    private readonly AgentflowRuntimeService _agentflowRuntimeService;

    public AgentflowsController(AgentflowRuntimeService agentflowRuntimeService)
    {
        _agentflowRuntimeService = agentflowRuntimeService;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync()
    {
        var agentflows = await _agentflowRuntimeService.ListAsync();
        return Ok(agentflows);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var agentflow = await _agentflowRuntimeService.GetAsync(id);
        return agentflow == null ? NotFound() : Ok(agentflow);
    }

    [HttpGet("mermaid/{id:guid}")]
    public async Task<IActionResult> GetMermaidAsync(Guid id)
    {
        var text = await _agentflowRuntimeService.GetMermaidAsync(id);
        return text == null ? NotFound() : Ok(text);
    }

    [HttpGet("{id:guid}/nodes")]
    public async Task<IActionResult> ListNodesAsync(Guid id)
    {
        var agentflow = await _agentflowRuntimeService.GetAsync(id);
        if (agentflow == null)
        {
            return NotFound();
        }

        var nodes = await _agentflowRuntimeService.ListNodesAsync(id);
        return Ok(nodes);
    }

    [HttpGet("{id:guid}/edges")]
    public async Task<IActionResult> ListEdgesAsync(Guid id)
    {
        var agentflow = await _agentflowRuntimeService.GetAsync(id);
        if (agentflow == null)
        {
            return NotFound();
        }

        var edges = await _agentflowRuntimeService.ListEdgesAsync(id);
        return Ok(edges);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] AgentflowCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var agentflow = new Agentflow
        {
            Name = request.Name,
            Description = request.Description,
            Pattern = request.Pattern,
            ConfigurationJson = request.ConfigurationJson,
            Enable = request.Enable
        };

        var nodes = request.Nodes
            .Select(x => new AgentflowNode
            {
                NodeId = x.NodeId,
                Type = x.Type,
                RelateId = x.RelateId,
            })
            .ToList();
        var edges = request.Edges
            .Select(x => new AgentflowEdge
            {
                EdgeId = x.EdgeId,
                SourceNodeId = x.SourceNodeId,
                TargetNodeId = x.TargetNodeId,
                Animated = x.Animated,
            })
            .ToList();

        var created = await _agentflowRuntimeService.CreateAsync(agentflow, nodes, edges, user);
        return created == null
            ? BadRequest("Failed to create agentflow (validation failed or referenced resources not found).")
            : Ok(created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] AgentflowUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var nodes = request.Nodes
            .Select(x => new AgentflowNode
            {
                NodeId = x.NodeId,
                Type = x.Type,
                RelateId = x.RelateId,
            })
            .ToList();
        var edges = request.Edges
            .Select(x => new AgentflowEdge
            {
                EdgeId = x.EdgeId,
                SourceNodeId = x.SourceNodeId,
                TargetNodeId = x.TargetNodeId,
                Animated = x.Animated,
            })
            .ToList();

        var updated = await _agentflowRuntimeService.UpdateAsync(
            id,
            agentflow =>
            {
                agentflow.Name = request.Name;
                agentflow.Description = request.Description;
                agentflow.Pattern = request.Pattern;
                agentflow.ConfigurationJson = request.ConfigurationJson;
                agentflow.Enable = request.Enable;
            },
            nodes,
            edges,
            user);

        return updated == null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _agentflowRuntimeService.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}
