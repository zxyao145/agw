using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Projects.Controllers;

[ApiController]
[Route("api/projects/conversation-image")]
public sealed class ProjectConversationImagesController : ControllerBase
{
    private readonly ProjectConversationImageAppService _images;

    public ProjectConversationImagesController(ProjectConversationImageAppService images)
    {
        _images = images;
    }

    [HttpGet]
    [ProducesApiResult(typeof(ProjectConversationImageResponse))]
    public async Task<IActionResult> GetAsync(
        [FromQuery] Guid projectId,
        [FromQuery] Guid conversationId,
        [FromQuery] string messageId,
        [FromQuery] string path,
        CancellationToken cancellationToken
    ) =>
        ApiResult.Ok(
            new ProjectConversationImageResponse(
                await _images.GetDataUriAsync(projectId, conversationId, messageId, path, cancellationToken)
            )
        );
}
