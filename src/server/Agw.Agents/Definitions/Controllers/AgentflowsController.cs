using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Execution.Agentflows;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Agents.Definitions.Controllers;

[ApiController]
[Route("api/agentflows")]
public class AgentflowsController : ControllerBase
{
    private readonly AgentflowAppService _agentflowAppService;
    private readonly IAgentflowRuntimeService _agentflowRuntimeService;

    public AgentflowsController(
        AgentflowAppService agentflowAppService,
        IAgentflowRuntimeService agentflowRuntimeService)
    {
        _agentflowAppService = agentflowAppService;
        _agentflowRuntimeService = agentflowRuntimeService;
    }

    [HttpGet]
    [ProducesApiResult(typeof(Agentflow[]))]
    public async Task<IActionResult> ListAsync()
    {
        var agentflows = await _agentflowAppService.ListAsync();
        return AgwApiResult.Ok(agentflows);
    }

    [HttpGet("{id:guid}")]
    [ProducesApiResult(typeof(Agentflow))]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var agentflow = await _agentflowAppService.GetAsync(id);
        return agentflow == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(agentflow);
    }

    [HttpGet("mermaid/{id:guid}")]
    [ProducesApiResult(typeof(string))]
    public async Task<IActionResult> GetMermaidAsync(Guid id)
    {
        var text = await _agentflowRuntimeService.GetMermaidAsync(id);
        return text == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(text);
    }

    [HttpGet("{id:guid}/nodes")]
    [ProducesApiResult(typeof(AgentflowNode[]))]
    public async Task<IActionResult> ListNodesAsync(Guid id)
    {
        var agentflow = await _agentflowAppService.GetAsync(id);
        if (agentflow == null)
        {
            return AgwApiResult.NotFound();
        }

        var nodes = await _agentflowAppService.ListNodesAsync(id);
        return AgwApiResult.Ok(nodes);
    }

    [HttpGet("{id:guid}/edges")]
    [ProducesApiResult(typeof(AgentflowEdge[]))]
    public async Task<IActionResult> ListEdgesAsync(Guid id)
    {
        var agentflow = await _agentflowAppService.GetAsync(id);
        if (agentflow == null)
        {
            return AgwApiResult.NotFound();
        }

        var edges = await _agentflowAppService.ListEdgesAsync(id);
        return AgwApiResult.Ok(edges);
    }

    [HttpPost]
    [ProducesApiResult(typeof(Agentflow))]
    public async Task<IActionResult> CreateAsync([FromBody] AgentflowCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var agentflow = new Agentflow
        {
            Name = request.Name,
            Description = request.Description,
            SummaryModelProviderId = request.SummaryModelProviderId
        };

        var nodes = request.Nodes
            .Select(x => new AgentflowNode
            {
                NodeId = x.NodeId,
                Kind = x.Kind,
                RelateId = x.RelateId,
                Name = x.Name,
                PositionJson = x.PositionJson,
                Instructions = x.Instructions,
                ConfigJson = x.ConfigJson,
            })
            .ToList();
        var edges = request.Edges
            .Select(x => new AgentflowEdge
            {
                EdgeId = x.EdgeId,
                SourceNodeId = x.SourceNodeId,
                TargetNodeId = x.TargetNodeId,
                Kind = x.Kind,
                Label = x.Label,
                ConditionJson = x.ConditionJson,
                ConfigJson = x.ConfigJson,
            })
            .ToList();

        var created = await _agentflowAppService.CreateAsync(agentflow, nodes, edges, user);
        return created == null
            ? AgwApiResult.BadRequest("Failed to create agentflow (validation failed or referenced resources not found).")
            : AgwApiResult.Ok(created);
    }

    [HttpPut("{id:guid}")]
    [ProducesApiResult(typeof(Agentflow))]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] AgentflowUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var nodes = request.Nodes
            .Select(x => new AgentflowNode
            {
                NodeId = x.NodeId,
                Kind = x.Kind,
                RelateId = x.RelateId,
                Name = x.Name,
                PositionJson = x.PositionJson,
                Instructions = x.Instructions,
                ConfigJson = x.ConfigJson,
            })
            .ToList();
        var edges = request.Edges
            .Select(x => new AgentflowEdge
            {
                EdgeId = x.EdgeId,
                SourceNodeId = x.SourceNodeId,
                TargetNodeId = x.TargetNodeId,
                Kind = x.Kind,
                Label = x.Label,
                ConditionJson = x.ConditionJson,
                ConfigJson = x.ConfigJson,
            })
            .ToList();

        var updated = await _agentflowAppService.UpdateAsync(
            id,
            agentflow =>
            {
                agentflow.Name = request.Name;
                agentflow.Description = request.Description;
                agentflow.SummaryModelProviderId = request.SummaryModelProviderId;
            },
            nodes,
            edges,
            user);

        return updated == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _agentflowAppService.DeleteAsync(id);
        return deleted ? AgwApiResult.Ok() : AgwApiResult.NotFound();
    }
}
