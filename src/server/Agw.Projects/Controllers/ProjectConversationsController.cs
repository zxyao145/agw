using Agw.Projects.Application;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Projects.Controllers;

[ApiController]
[Route("api/projects/{projectId}/conversations")]
public class ProjectConversationsController : ControllerBase
{
    private readonly ProjectConversationAppService _projectConversationAppService;

    public ProjectConversationsController(ProjectConversationAppService projectConversationAppService)
    {
        _projectConversationAppService = projectConversationAppService;
    }

    [HttpGet]
    [ProducesApiResult(typeof(ProjectConversationSummaryResponse[]))]
    public async Task<IActionResult> ListAsync(Guid projectId) =>
        ApiResult.Ok(await _projectConversationAppService.ListResponsesAsync(projectId));

    [HttpGet("{conversationId}")]
    [ProducesApiResult(typeof(ProjectConversationResponse))]
    public async Task<IActionResult> GetAsync(Guid projectId, Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _projectConversationAppService.GetResponseAsync(
            projectId,
            conversationId,
            cancellationToken
        );
        return conversation == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(conversation);
    }

    [HttpGet("{conversationId}/messages")]
    [ProducesApiResult(typeof(ProjectConversationMessagePageResponse))]
    public async Task<IActionResult> GetMessagesAsync(
        Guid projectId,
        Guid conversationId,
        [FromQuery] ProjectConversationMessagesQuery query,
        CancellationToken cancellationToken
    )
    {
        var page = await _projectConversationAppService.GetMessagePageAsync(
            projectId,
            conversationId,
            query,
            cancellationToken
        );
        return page == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(page);
    }

    [HttpDelete("{conversationId}/clear-records")]
    [ProducesApiResult]
    public async Task<IActionResult> ClearRecordsAsync(Guid projectId, Guid conversationId)
    {
        var result = await _projectConversationAppService.ClearRecordsAsync(projectId, conversationId);
        return result.Type == ApplicationResultType.Success
            ? ApiResult.Ok()
            : ErrorCodes.ResourceNotFound.ToApiResult();
    }

    [HttpPut("{conversationId}/title")]
    [ProducesApiResult]
    public async Task<IActionResult> UpdateTitleAsync(
        Guid projectId,
        Guid conversationId,
        [FromBody] ProjectConversationTitleUpdateRequest request
    )
    {
        var user = User.GetUserId();
        var result = await _projectConversationAppService.UpdateTitleAsync(
            projectId,
            conversationId,
            request.Title,
            user
        );

        return result.Type switch
        {
            ApplicationResultType.Success => ApiResult.Ok(),
            ApplicationResultType.NotFound => ErrorCodes.ResourceNotFound.ToApiResult(),
            ApplicationResultType.Invalid => ApiResult.BadRequest(
                result.Error ?? "Invalid request.",
                ErrorCodes.InvalidParam.Code
            ),
            _ => ApiResult.BadRequest(result.Error ?? "Invalid request.", ErrorCodes.InvalidParam.Code),
        };
    }

    [HttpDelete]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAllAsync(Guid projectId)
    {
        var result = await _projectConversationAppService.DeleteAllAsync(projectId);
        return result.Type == ApplicationResultType.Success
            ? ApiResult.Ok()
            : ErrorCodes.ResourceNotFound.ToApiResult();
    }

    [HttpDelete("{conversationId}")]
    [ProducesApiResult]
    public async Task<IActionResult> DeleteAsync(Guid projectId, Guid conversationId)
    {
        var deleted = await _projectConversationAppService.DeleteAsync(projectId, conversationId);
        return deleted ? ApiResult.Ok() : ErrorCodes.ResourceNotFound.ToApiResult();
    }
}
