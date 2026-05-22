using Agw.Domain.Services;
using Agw.Shared.Models;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Manager.Api.Controllers;

[ApiController]
[Route("api/tools")]
public class ToolsController : ControllerBase
{
    private readonly ToolRegistryService _toolRegistry;

    public ToolsController(ToolRegistryService toolRegistry)
    {
        _toolRegistry = toolRegistry;
    }

    /// <summary>
    /// Gets all available tools.
    /// </summary>
    [HttpGet]
    [ProducesApiResult(typeof(ToolInfo[]))]
    public IActionResult GetAllTools()
    {
        var tools = _toolRegistry.GetAllTools();
        return AgwApiResult.Ok(tools);
    }

    /// <summary>
    /// Gets tools grouped by category.
    /// </summary>
    [HttpGet("by-category")]
    [ProducesApiResult(typeof(Dictionary<string, List<ToolInfo>>))]
    public IActionResult GetToolsByCategory()
    {
        var toolsByCategory = _toolRegistry.GetToolsByCategory();
        return AgwApiResult.Ok(toolsByCategory);
    }

    /// <summary>
    /// Gets a specific tool by name.
    /// </summary>
    [HttpGet("{name}")]
    [ProducesApiResult(typeof(ToolInfo))]
    public IActionResult GetTool(string name)
    {
        var tool = _toolRegistry.GetTool(name);
        return tool == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(tool);
    }
}
