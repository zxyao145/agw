using Agw.Auth.Contracts;
using Agw.Projects.Application.History;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

/// <summary>
/// 按 Turn 分页读取对话：Turn 列表与单个 Turn 的全部消息，只返回当前用户对话中的数据。
/// Reads a conversation turn by turn: the turn list and all messages of one turn, returning only data of the current user's conversations.
/// </summary>
public sealed class ConversationTurnQueryService
{
    private const int InputSummaryLength = 200;

    private readonly IProjectsDbContext _dbContext;
    private readonly IUserInfoService _userInfoService;

    public ConversationTurnQueryService(IProjectsDbContext dbContext, IUserInfoService userInfoService)
    {
        _dbContext = dbContext;
        _userInfoService = userInfoService;
    }

    public async Task<ConversationTurnPageResponse?> ListAsync(
        ConversationTurnListQuery query,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!await IsOwnedConversationAsync(query.ConversationId, cancellationToken).ConfigureAwait(false))
            return null;
        var turns = _dbContext
            .ProjectConversationTurns.AsNoTracking()
            .Where(turn => turn.ProjectConversationId == query.ConversationId);
        if (query.BeforeSequence is { } before)
            turns = turns.Where(turn => turn.FirstSequence < before);
        var page = await turns
            .OrderByDescending(turn => turn.FirstSequence)
            .ThenByDescending(turn => turn.Id)
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasMore = page.Count > query.Limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var inputIds = page.Select(turn => turn.InputMessageId).ToArray();
        var inputs = await _dbContext
            .ProjectConversationChatHistories.AsNoTracking()
            .Where(record => record.ConversationId == query.ConversationId && inputIds.Contains(record.Id))
            .ToDictionaryAsync(record => record.Id, cancellationToken)
            .ConfigureAwait(false);
        return new ConversationTurnPageResponse(
            page.Select(turn => ToResponse(turn, inputs.GetValueOrDefault(turn.InputMessageId))).ToList(),
            hasMore ? page[^1].FirstSequence : null,
            hasMore
        );
    }

    public async Task<ConversationTurnMessagesResponse?> GetMessagesAsync(
        ConversationTurnMessagesQuery query,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!await IsOwnedConversationAsync(query.ConversationId, cancellationToken).ConfigureAwait(false))
            return null;
        var exists = await _dbContext
            .ProjectConversationTurns.AsNoTracking()
            .AnyAsync(
                turn => turn.Id == query.TurnId && turn.ProjectConversationId == query.ConversationId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!exists)
            return null;
        var records = await _dbContext
            .ProjectConversationChatHistories.AsNoTracking()
            .Where(record =>
                record.ConversationId == query.ConversationId
                && record.TurnId == query.TurnId
                && record.ConversationPayload != null
                && record.ConversationSequence != null
            )
            .OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new ConversationTurnMessagesResponse(
            query.TurnId,
            records
                .SelectMany(record =>
                    TaskExecutionMapper
                        .ToAiMessages(record)
                        .Select(message => new ConversationTurnMessageResponse(
                            record.ConversationSequence!.Value,
                            record.StepIndex,
                            message
                        ))
                )
                .ToList()
        );
    }

    private Task<bool> IsOwnedConversationAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var owner = _userInfoService.RequiredUserId;
        return _dbContext
            .ProjectConversations.AsNoTracking()
            .AnyAsync(
                conversation =>
                    conversation.Id == conversationId
                    && conversation.CreateBy == owner
                    && conversation.Project!.CreateBy == owner,
                cancellationToken
            );
    }

    private static ConversationTurnResponse ToResponse(
        ProjectConversationTurn turn,
        ProjectConversationChatHistory? input
    )
    {
        var text = input?.GetText() ?? string.Empty;
        return new ConversationTurnResponse(
            turn.Id,
            turn.ProjectConversationId,
            turn.Status.ToString().ToLowerInvariant(),
            turn.TargetId,
            turn.RuntimeType == AgentRuntimeType.Agentflow ? "agentflow" : "agent",
            turn.StartedAt,
            turn.FinishedAt,
            turn.StepCount,
            turn.FirstSequence,
            turn.LastSequence,
            turn.ErrorCode,
            turn.InputMessageId,
            text.Length <= InputSummaryLength ? text : text[..InputSummaryLength]
        );
    }
}
