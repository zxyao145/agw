using System.Text.RegularExpressions;
using Agw.Auth.Contracts;
using Agw.Projects.Application.History;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Application;

/// <summary>
/// 按 Turn 分页读取对话：Turn 列表与单个 Turn 的全部消息，只返回当前用户对话中的数据。
/// Reads a conversation turn by turn: the turn list and all messages of one turn, returning only data of the current user's conversations.
/// </summary>
public sealed partial class ConversationTurnQueryService
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
        if (query.BeforeSequence.HasValue != query.BeforeTurnId.HasValue)
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "beforeSequence and beforeTurnId must be provided together."
            );
        if (!await IsOwnedConversationAsync(query.ConversationId, cancellationToken).ConfigureAwait(false))
            return null;
        var turns = _dbContext
            .ProjectConversationTurns.AsNoTracking()
            .Where(turn => turn.ProjectConversationId == query.ConversationId);
        // 没有输入的 Turn 不写入消息，后续 Turn 可能与它共用 first_sequence；游标按 (first_sequence, turnId) 与排序一致。
        // A turn without input writes no message, so a later turn can share its first_sequence; the cursor follows the (first_sequence, turnId) order.
        if (query.BeforeSequence is { } beforeSequence && query.BeforeTurnId is { } beforeTurnId)
            turns = turns.Where(turn =>
                turn.FirstSequence < beforeSequence
                || turn.FirstSequence == beforeSequence && turn.Id.CompareTo(beforeTurnId) < 0
            );
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
            hasMore ? page[^1].Id : null,
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
        var visibleRecords = await ConversationInputCopyFilter.FilterAsync(
            _dbContext,
            query.ConversationId,
            records,
            cancellationToken
        );
        return new ConversationTurnMessagesResponse(
            query.TurnId,
            visibleRecords
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

    /// <summary>
    /// 计算当前用户项目中每个会话的执行状态：存在活动 Turn 时为 running，否则按最新 Turn 映射，Completed 或没有 Turn 的会话不返回。
    /// Turn 按 (first_sequence, turnId) 倒序比较，与 ListAsync 相同。项目不属于当前用户时返回 null。
    /// Computes the execution status of each conversation in the current user's project: running when an active turn exists, otherwise mapped from the latest turn; Completed or turn-less conversations are omitted.
    /// Turns compare in descending (first_sequence, turnId) order, as in ListAsync. Returns null when the project is not the current user's.
    /// </summary>
    public async Task<ConversationActivityResponse?> GetActivityAsync(
        ConversationActivityQuery query,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        var owner = _userInfoService.RequiredUserId;
        var ownsProject = await _dbContext
            .Projects.AsNoTracking()
            .AnyAsync(project => project.Id == query.ProjectId && project.CreateBy == owner, cancellationToken)
            .ConfigureAwait(false);
        if (!ownsProject)
            return null;
        var conversationIds = _dbContext
            .ProjectConversations.AsNoTracking()
            .Where(conversation => conversation.ProjectId == query.ProjectId && conversation.CreateBy == owner)
            .Select(conversation => conversation.Id);
        var turns = _dbContext
            .ProjectConversationTurns.AsNoTracking()
            .Where(turn => conversationIds.Contains(turn.ProjectConversationId));
        var activeTurns = await turns
            .Where(turn =>
                turn.Status == ProjectConversationTurnStatus.Accepted
                || turn.Status == ProjectConversationTurnStatus.Running
            )
            .OrderByDescending(turn => turn.FirstSequence)
            .ThenByDescending(turn => turn.Id)
            .Select(turn => new { turn.ProjectConversationId, turn.Id })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var latestUnsuccessfulTurns = await turns
            .Where(turn =>
                !_dbContext.ProjectConversationTurns.Any(newer =>
                    newer.ProjectConversationId == turn.ProjectConversationId
                    && (
                        newer.FirstSequence > turn.FirstSequence
                        || newer.FirstSequence == turn.FirstSequence && newer.Id.CompareTo(turn.Id) > 0
                    )
                )
            )
            .Where(turn =>
                turn.Status == ProjectConversationTurnStatus.Failed
                || turn.Status == ProjectConversationTurnStatus.Interrupted
            )
            .Select(turn => new
            {
                turn.ProjectConversationId,
                turn.Id,
                turn.Status,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = new List<ConversationActivityItemResponse>();
        var running = new HashSet<Guid>();
        // 活动 Turn 已按倒序排列，每个会话取第一个。
        // Active turns are already in descending order; each conversation takes its first one.
        foreach (var turn in activeTurns)
        {
            if (running.Add(turn.ProjectConversationId))
                items.Add(new ConversationActivityItemResponse(turn.ProjectConversationId, turn.Id, "running"));
        }
        foreach (var turn in latestUnsuccessfulTurns)
        {
            if (!running.Contains(turn.ProjectConversationId))
                items.Add(
                    new ConversationActivityItemResponse(
                        turn.ProjectConversationId,
                        turn.Id,
                        turn.Status.ToString().ToLowerInvariant()
                    )
                );
        }
        return new ConversationActivityResponse(items);
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
            GetInputSummary(input)
        );
    }

    private static string GetInputSummary(ProjectConversationChatHistory? input)
    {
        var message = input?.ToChatMessage();
        if (message == null)
            return string.Empty;

        var text = string.Join(" ", message.Contents.OfType<TextContent>().Select(content => content.Text));
        if (string.IsNullOrWhiteSpace(text))
        {
            var images = message
                .Contents.OfType<DataContent>()
                .Where(content => content.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var names = images.Select(content => content.Name?.Trim()).Where(name => !string.IsNullOrEmpty(name));
            text = string.Join(", ", names);
            if (text.Length == 0)
                text =
                    images.Count > 0
                        ? "Image input"
                        : message.Contents.OfType<UriContent>().FirstOrDefault()?.Uri.ToString() ?? "User input";
        }

        text = WhitespaceRegex().Replace(text, " ").Trim();
        // 按 Unicode 标量截取：累加前 InputSummaryLength 个 Rune 的 UTF-16 长度，只分配一次结果字符串。
        // Truncates by Unicode scalar: sums the UTF-16 length of the first InputSummaryLength runes and allocates the result once.
        var length = 0;
        var runes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (runes++ == InputSummaryLength)
                break;
            length += rune.Utf16SequenceLength;
        }
        return text[..length];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
