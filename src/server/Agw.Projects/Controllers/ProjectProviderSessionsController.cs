using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Projects.Controllers;

/// <summary>
/// 查询对话中 External Agent 的 provider session 绑定，并归档指定记录，使该 Agent 下一次执行创建新的 provider session。
/// Lists the External Agent provider session bindings of a conversation and archives a specified record so the Agent's next run starts a new provider session.
/// </summary>
[ApiController]
[Route("api/projects/conversations/provider-sessions")]
public class ProjectProviderSessionsController : ControllerBase
{
    private readonly ITaskSessionBindingService _bindings;

    public ProjectProviderSessionsController(ITaskSessionBindingService bindings)
    {
        _bindings = bindings;
    }

    [HttpGet]
    [ProducesApiResult(typeof(IReadOnlyList<ProviderSessionResponse>))]
    public async Task<IActionResult> ListAsync(
        [FromQuery] ProviderSessionListQuery query,
        CancellationToken cancellationToken
    )
    {
        var bindings = await _bindings.ListAsync(query.ProjectId, query.ConversationId, cancellationToken);
        return ApiResult.Ok(bindings.Select(ProviderSessionResponse.FromDomain).ToList());
    }

    [HttpPost("archive")]
    [ProducesApiResult]
    public async Task<IActionResult> ArchiveAsync(
        [FromBody] ProviderSessionArchiveRequest request,
        CancellationToken cancellationToken
    )
    {
        await _bindings.ArchiveAsync(request.ProjectId, request.ConversationId, request.BindingId, cancellationToken);
        return ApiResult.Ok();
    }
}
