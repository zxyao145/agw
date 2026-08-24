using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Tools;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Projects.Controllers;

[ApiController]
[Route("api/projects")]
public class ProjectsController : ControllerBase
{
    private readonly IProjectAppService _projectAppService;

    public ProjectsController(IProjectAppService projectAppService)
    {
        _projectAppService = projectAppService;
    }

    [HttpGet]
    [ProducesApiResult(typeof(ProjectResponse[]))]
    public async Task<IActionResult> ListAsync()
    {
        var projects = await _projectAppService.ListForCurrentUserAsync();
        return ApiResult.Ok(projects.Select(ProjectResponse.FromDomain).ToArray());
    }

    [HttpGet("{id:guid}")]
    [ProducesApiResult(typeof(ProjectResponse))]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var project = await _projectAppService.GetForCurrentUserAsync(id);
        return project == null
            ? ErrorCodes.ResourceNotFound.ToApiResult()
            : ApiResult.Ok(ProjectResponse.FromDomain(project));
    }

    [HttpPost]
    [ProducesApiResult(typeof(ProjectResponse))]
    public async Task<IActionResult> CreateAsync([FromBody] ProjectCreateRequest request)
    {
        var toolsError = ToolValueObjectValidation.GetError(request.Tools);
        if (toolsError != null)
        {
            return ApiResult.BadRequest(toolsError, ErrorCodes.InvalidParam.Code);
        }

        if (ContainsAgentOnlyToolBlock(request.Tools))
        {
            return ApiResult.BadRequest(
                $"Tool Block '{ToolBlockDefinitionNames.BackgroundAgents}' can only be configured on an Agent.",
                ErrorCodes.InvalidParam.Code
            );
        }

        var project = new Project
        {
            Name = request.Name,
            Description = request.Description,
            Workspace = request.Workspace,
            ExtraSetting = request.ExtraSetting,
            Tools = request.Tools ?? [],
            EnvironmentVariables = request.EnvironmentVariables ?? new Dictionary<string, string>(),
        };

        var created = await _projectAppService.CreateAsync(
            project,
            request.McpToolServerIds,
            request.SkillIds,
            request.ConnectionIds
        );
        if (created == null)
        {
            return ApiResult.BadRequest("Failed to create project.", ErrorCodes.InvalidParam.Code);
        }

        return ApiResult.Ok(ProjectResponse.FromDomain(created));
    }

    [HttpPut("{id:guid}")]
    [ProducesApiResult(typeof(ProjectResponse))]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ProjectUpdateRequest request)
    {
        var toolsError = ToolValueObjectValidation.GetError(request.Tools);
        if (toolsError != null)
        {
            return ApiResult.BadRequest(toolsError, ErrorCodes.InvalidParam.Code);
        }

        if (ContainsAgentOnlyToolBlock(request.Tools))
        {
            return ApiResult.BadRequest(
                $"Tool Block '{ToolBlockDefinitionNames.BackgroundAgents}' can only be configured on an Agent.",
                ErrorCodes.InvalidParam.Code
            );
        }

        var updated = await _projectAppService.UpdateAsync(
            id,
            project =>
            {
                project.Name = request.Name;
                project.Description = request.Description;
                project.Workspace = request.Workspace;
                project.ExtraSetting = request.ExtraSetting;
                if (request.Tools != null)
                {
                    project.Tools = request.Tools;
                }
                if (request.EnvironmentVariables != null)
                {
                    project.EnvironmentVariables = request.EnvironmentVariables;
                }
            },
            request.McpToolServerIds,
            request.SkillIds,
            request.ConnectionIds
        );

        return updated == null
            ? ErrorCodes.ResourceNotFound.ToApiResult()
            : ApiResult.Ok(ProjectResponse.FromDomain(updated));
    }

    [HttpDelete("{id:guid}")]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _projectAppService.DeleteAsync(id);
        return deleted ? ApiResult.Ok() : ErrorCodes.ResourceNotFound.ToApiResult();
    }

    private static bool ContainsAgentOnlyToolBlock(IReadOnlyList<ToolValueObject>? values) =>
        values?.Any(static value => value is ToolBlockValue { Definition: BackgroundAgentsToolBlockDefinition })
        == true;
}
