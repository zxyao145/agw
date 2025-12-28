using DSystem.Api.Contracts;
using DSystem.Domain.Entities;
using DSystem.Domain.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSystem.Api.Controllers;

[ApiController]
[Route("api/projects")]
public class ProjectsController : ControllerBase
{
    private readonly ProjectDomainService _projectService;

    public ProjectsController(ProjectDomainService projectService)
    {
        _projectService = projectService;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync()
    {
        var projects = await _projectService.ListAsync();
        return Ok(projects);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var project = await _projectService.GetAsync(id);
        return project == null ? NotFound() : Ok(project);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] ProjectCreateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";
        var project = new Project
        {
            Name = request.Name,
            Description = request.Description,
            Enable = request.Enable
        };

        var created = await _projectService.CreateAsync(project, user);
        if (created == null)
        {
            return BadRequest("Failed to create project.");
        }

        return Ok(created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ProjectUpdateRequest request)
    {
        var user = User?.Identity?.Name ?? "system";

        var updated = await _projectService.UpdateAsync(id, project =>
        {
            project.Name = request.Name;
            project.Description = request.Description;
            project.Enable = request.Enable;
        }, user);

        return updated == null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _projectService.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}