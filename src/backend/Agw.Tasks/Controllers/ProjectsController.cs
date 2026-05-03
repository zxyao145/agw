using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Tasks.Controllers;

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
    public async Task<IActionResult> ListAsync()
    {
        var projects = await _projectAppService.ListAsync();
        return AgwApiResult.Ok(projects);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var project = await _projectAppService.GetAsync(id);
        return project == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(project);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] ProjectCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var project = new Project
        {
            Name = request.Name,
            Description = request.Description,
            Workspace = request.Workspace,
            Enable = request.Enable,
            ExtraSetting = request.ExtraSetting
        };

        var created = await _projectAppService.CreateAsync(project, user);
        if (created == null)
        {
            return AgwApiResult.BadRequest("Failed to create project.");
        }

        return AgwApiResult.Ok(created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ProjectUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";

        var updated = await _projectAppService.UpdateAsync(id, project =>
        {
            project.Name = request.Name;
            project.Description = request.Description;
            project.Workspace = request.Workspace;
            project.Enable = request.Enable;
            project.ExtraSetting = request.ExtraSetting;
        }, user);

        return updated == null ? AgwApiResult.NotFound() : AgwApiResult.Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _projectAppService.DeleteAsync(id);
        return deleted ? AgwApiResult.Ok() : AgwApiResult.NotFound();
    }
}
