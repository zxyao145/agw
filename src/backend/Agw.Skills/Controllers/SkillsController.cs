using Agw.Shared.Data.Entities.Skills;
using Agw.Skills.Application;
using Agw.Skills.Contracts.Manager;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Skills.Controllers;

[ApiController]
[Route("api/skills")]
public class SkillsController : ControllerBase
{
    private readonly SkillAppService _skillAppService;

    public SkillsController(SkillAppService skillAppService)
    {
        _skillAppService = skillAppService;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync()
    {
        var skills = await _skillAppService.ListAsync();
        return Ok(skills.Select(Map));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var skill = await _skillAppService.GetAsync(id);
        return skill == null ? NotFound() : Ok(Map(skill));
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestFormLimits(MultipartBodyLengthLimit = 104857600)]
    public async Task<IActionResult> CreateAsync([FromForm] SkillCreateRequest request)
    {
        try
        {
            var user = User?.Identity?.Name ?? "system";
            var skill = new Skill
            {
                Name = request.Name,
                Description = request.Description,
            };

            var created = await _skillAppService.CreateAsync(skill, request.Archive, user);
            return Ok(Map(created));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{id:guid}")]
    [Consumes("multipart/form-data")]
    [RequestFormLimits(MultipartBodyLengthLimit = 104857600)]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromForm] SkillUpdateRequest request)
    {
        try
        {
            var user = User?.Identity?.Name ?? "system";
            var updated = await _skillAppService.UpdateAsync(id, request.Name, request.Description, request.Archive, user);
            return updated == null ? NotFound() : Ok(Map(updated));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _skillAppService.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }

    private static SkillResponse Map(SkillDetails detail)
    {
        var skill = detail.Skill;
        return new SkillResponse(
            skill.Id,
            skill.Name,
            skill.Description,
            skill.ContentPath,
            detail.AgentIds,
            skill.CreateTime,
            skill.CreateBy,
            skill.UpdateTime,
            skill.UpdateBy);
    }
}
