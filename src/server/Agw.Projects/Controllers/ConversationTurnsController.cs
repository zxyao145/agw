using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Projects.Controllers;

/// <summary>
/// 按 Turn 加载对话：客户端先取最近若干 Turn，向上滚动时取更早的 Turn，再按需取每个 Turn 的消息。
/// Loads a conversation by turn: clients fetch the latest turns first, older turns while scrolling up, and each turn's messages on demand.
/// </summary>
[ApiController]
[Route("api/projects")]
public class ConversationTurnsController : ControllerBase
{
    private readonly ConversationTurnQueryService _turns;

    public ConversationTurnsController(ConversationTurnQueryService turns)
    {
        _turns = turns;
    }

    [HttpGet("conversation-turns")]
    [ProducesApiResult(typeof(ConversationTurnPageResponse))]
    public async Task<IActionResult> ListAsync(
        [FromQuery] ConversationTurnListQuery query,
        CancellationToken cancellationToken
    )
    {
        var page = await _turns.ListAsync(query, cancellationToken);
        return page == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(page);
    }

    [HttpGet("conversation-turn-messages")]
    [ProducesApiResult(typeof(ConversationTurnMessagesResponse))]
    public async Task<IActionResult> GetMessagesAsync(
        [FromQuery] ConversationTurnMessagesQuery query,
        CancellationToken cancellationToken
    )
    {
        var messages = await _turns.GetMessagesAsync(query, cancellationToken);
        return messages == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(messages);
    }

    /// <summary>
    /// 项目中各会话的执行状态快照，只返回 running、failed、interrupted 的会话。
    /// A snapshot of the project's conversation execution statuses, returning only running, failed and interrupted conversations.
    /// </summary>
    [HttpGet("conversation-activity")]
    [ProducesApiResult(typeof(ConversationActivityResponse))]
    public async Task<IActionResult> GetActivityAsync(
        [FromQuery] ConversationActivityQuery query,
        CancellationToken cancellationToken
    )
    {
        var activity = await _turns.GetActivityAsync(query, cancellationToken);
        return activity == null ? ErrorCodes.ResourceNotFound.ToApiResult() : ApiResult.Ok(activity);
    }
}
