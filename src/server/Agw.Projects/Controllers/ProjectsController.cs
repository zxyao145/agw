using Agw.Shared;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Projects;
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
        var projects = await _projectAppService.ListAsync();
        return ApiResult.Ok(projects.Select(ProjectResponse.FromDomain).ToArray());
    }

    [HttpGet("{id:guid}")]
    [ProducesApiResult(typeof(ProjectResponse))]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var project = await _projectAppService.GetAsync(id);
        return project == null
            ? ErrorCodes.ResourceNotFound.ToApiResult()
            : ApiResult.Ok(ProjectResponse.FromDomain(project));
    }

    [HttpPost]
    [ProducesApiResult(typeof(ProjectResponse))]
    public async Task<IActionResult> CreateAsync([FromBody] ProjectCreateRequest request)
    {
        var user = User?.Identity?.Name ?? Constants.AdminUserName;
        var project = new Project
        {
            Name = request.Name,
            Description = request.Description,
            Workspace = request.Workspace,
            ExtraSetting = request.ExtraSetting,
            Tools = request.Tools,
            EnvironmentVariables = request.EnvironmentVariables ?? new Dictionary<string, string>()
        };

        var created = await _projectAppService.CreateAsync(
            project,
            request.McpToolServerIds,
            request.SkillIds,
            request.ConnectionIds,
            user);
        if (created == null)
        {
            return ApiResult.BadRequest(
                "Failed to create project.",
                ErrorCodes.InvalidParam.Code);
        }

        return ApiResult.Ok(ProjectResponse.FromDomain(created));
    }

    [HttpPut("{id:guid}")]
    [ProducesApiResult(typeof(ProjectResponse))]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ProjectUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? Constants.AdminUserName;

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
            request.ConnectionIds,
            user);

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
}
