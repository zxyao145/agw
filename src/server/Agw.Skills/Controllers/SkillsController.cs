using Agw.Shared;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Agw.Skills.Application;
using Agw.Skills.Contracts.Manager;

using Bens.Results;

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
    [ProducesApiResult(typeof(SkillResponse[]))]
    public async Task<IActionResult> ListAsync()
    {
        var skills = await _skillAppService.ListAsync();
        return ApiResult.Ok(skills.Select(Map));
    }

    [HttpGet("paged")]
    [ProducesApiResult(typeof(PagedResult<SkillResponse>))]
    public async Task<IActionResult> ListPagedAsync(
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var page = await _skillAppService.ListPageAsync(pageIndex, pageSize, cancellationToken);
        return ApiResult.Ok(new PagedResult<SkillResponse>
        {
            Items = page.Items.Select(Map).ToList(),
            Total = page.Total,
            PageIndex = page.PageIndex,
            PageSize = page.PageSize,
        });
    }

    [HttpGet("{id:guid}")]
    [ProducesApiResult(typeof(SkillResponse))]
    public async Task<IActionResult> GetAsync(Guid id)
    {
        var skill = await _skillAppService.GetAsync(id);
        return skill == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(Map(skill));
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestFormLimits(MultipartBodyLengthLimit = 104857600)]
    [ProducesApiResult(typeof(SkillResponse))]
    public async Task<IActionResult> CreateAsync([FromForm] SkillCreateRequest request)
    {
        try
        {
            var user = User?.Identity?.Name ?? Constants.AdminUserName;
            var skill = new Skill
            {
                Name = request.Name,
                Description = request.Description,
            };

            var created = await _skillAppService.CreateAsync(skill, request.Archive, user);
            return ApiResult.Ok(Map(created));
        }
        catch (AgwException ex)
        {
            return ex.ToApiResult();
        }
    }

    [HttpPut("{id:guid}")]
    [Consumes("multipart/form-data")]
    [RequestFormLimits(MultipartBodyLengthLimit = 104857600)]
    [ProducesApiResult(typeof(SkillResponse))]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromForm] SkillUpdateRequest request)
    {
        try
        {
            var user = User?.Identity?.Name ?? Constants.AdminUserName;
            var updated = await _skillAppService.UpdateAsync(id, request.Name, request.Description, request.Archive, user);
            return updated == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(Map(updated));
        }
        catch (AgwException ex)
        {
            return ex.ToApiResult();
        }
    }

    [HttpDelete("{id:guid}")]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var deleted = await _skillAppService.DeleteAsync(id);
        return deleted ? ApiResult.Ok() : ErrorCodes.ResourceNotFound.ToApiResult();
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
