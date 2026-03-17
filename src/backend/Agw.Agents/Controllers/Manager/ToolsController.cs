using Agw.Domain.Services;
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
    public IActionResult GetAllTools()
    {
        var tools = _toolRegistry.GetAllTools();
        return Ok(tools);
    }

    /// <summary>
    /// Gets tools grouped by category.
    /// </summary>
    [HttpGet("by-category")]
    public IActionResult GetToolsByCategory()
    {
        var toolsByCategory = _toolRegistry.GetToolsByCategory();
        return Ok(toolsByCategory);
    }

    /// <summary>
    /// Gets a specific tool by name.
    /// </summary>
    [HttpGet("{name}")]
    public IActionResult GetTool(string name)
    {
        var tool = _toolRegistry.GetTool(name);
        return tool == null ? NotFound() : Ok(tool);
    }
}
