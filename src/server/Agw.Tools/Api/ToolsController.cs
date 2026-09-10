using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Tools.Api.Controllers;

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
    [ProducesApiResult(typeof(ToolLiteInfo[]))]
    public IActionResult GetAllTools()
    {
        var tools = _toolRegistry.GetAllTools().Select(ToLiteInfo).ToArray();
        return ApiResult.Ok(tools);
    }

    /// <summary>
    /// Gets tools grouped by category.
    /// </summary>
    [HttpGet("by-category")]
    [ProducesApiResult(typeof(Dictionary<string, List<ToolLiteInfo>>))]
    public IActionResult GetToolsByCategory()
    {
        var toolsByCategory = _toolRegistry
            .GetToolsByCategory()
            .ToDictionary(pair => pair.Key, pair => pair.Value.Select(ToLiteInfo).ToList());
        return ApiResult.Ok(toolsByCategory);
    }

    /// <summary>
    /// Gets a specific tool by name.
    /// </summary>
    [HttpGet("{name}")]
    [ProducesApiResult(typeof(ToolLiteInfo))]
    public IActionResult GetTool(string name)
    {
        var tool = _toolRegistry.GetTool(name);
        return tool == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(ToLiteInfo(tool));
    }

    private static ToolLiteInfo ToLiteInfo(ToolInfo tool) =>
        new()
        {
            Kind = tool.Kind,
            Name = tool.Name,
            DisplayName = tool.DisplayName,
            Description = tool.Description,
            Category = tool.Category,
            MemberToolNames = tool.MemberToolNames,
            Scopes = tool.Scopes,
            RequiresWorkspace = tool.RequiresWorkspace,
            RequiredPermission = tool.RequiredPermission,
            RequiresConfirmation = tool.RequiresConfirmation,
        };
}
