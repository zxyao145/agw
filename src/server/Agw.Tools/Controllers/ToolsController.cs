using Agw.Domain.Services;
using Agw.Shared.Contracts.Tools;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;

using Bens.Results;

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
        return ApiResult.Ok(tools);
    }

    /// <summary>
    /// Gets tools grouped by category.
    /// </summary>
    [HttpGet("by-category")]
    [ProducesApiResult(typeof(Dictionary<string, List<ToolInfo>>))]
    public IActionResult GetToolsByCategory()
    {
        var toolsByCategory = _toolRegistry.GetToolsByCategory();
        return ApiResult.Ok(toolsByCategory);
    }

    /// <summary>
    /// Gets a specific tool by name.
    /// </summary>
    [HttpGet("{name}")]
    [ProducesApiResult(typeof(ToolInfo))]
    public IActionResult GetTool(string name)
    {
        var tool = _toolRegistry.GetTool(name);
        return tool == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(tool);
    }
}
