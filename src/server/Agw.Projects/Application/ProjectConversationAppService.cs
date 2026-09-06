using System.Globalization;
using System.Text;
using System.Text.Json;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Domain.Services;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public class ProjectConversationAppService
{
    private readonly IProjectsDbContext _dbContext;
    private readonly ProjectResolver _projectResolver;
    private readonly IProjectDeletionCoordinator _deletionCoordinator;
    private readonly TimeProvider _timeProvider;

    public ProjectConversationAppService(
        IProjectsDbContext dbContext,
        ProjectResolver projectResolver,
        IProjectDeletionCoordinator deletionCoordinator,
        TimeProvider timeProvider
    )
    {
        _dbContext = dbContext;
        _projectResolver = projectResolver;
        _deletionCoordinator = deletionCoordinator;
        _timeProvider = timeProvider;
    }

    public async Task<PagedResult<ProjectConversationSummaryResponse>> ListResponsesAsync(
        Guid projectId,
        ProjectConversationListQuery query,
        CancellationToken cancellationToken = default
    )
    {
        ValidateConversationListQuery(query);
        var project = await _projectResolver.ResolveRequiredAsync(projectId, cancellationToken);
        if (project == null)
        {
            return CreateConversationPage([], 0, query);
        }

        var conversationsQuery = _dbContext
            .ProjectConversations.AsNoTracking()
            .Where(conversation => conversation.ProjectId == project.Id && conversation.CreateBy == project.CreateBy);
        if (query.ContextId != null)
        {
            conversationsQuery = conversationsQuery.Where(conversation => conversation.ContextId == query.ContextId);
        }

        var histories = _dbContext.ProjectConversationChatHistories.AsNoTracking();
        conversationsQuery = conversationsQuery.Where(conversation =>
            conversation.JobId == null
            || histories.Any(record =>
                record.ConversationId == conversation.Id
                && record.ConversationPayload != null
                && record.ConversationSequence != null
            )
            || !histories.Any(record => record.ConversationId == conversation.Id)
        );

        var total = await conversationsQuery.LongCountAsync(cancellationToken);
        if (total == 0)
        {
            return CreateConversationPage([], 0, query);
        }

        var conversations = await GetConversationPageAsync(conversationsQuery, query, cancellationToken);
        if (conversations.Count == 0)
        {
            return CreateConversationPage([], total, query);
        }

        var conversationIds = conversations.Select(conversation => conversation.Id).ToList();
        var aggregates = await histories
            .Where(record => conversationIds.Contains(record.ConversationId))
            .GroupBy(record => record.ConversationId)
            .Select(group => new ConversationListAggregate
            {
                ConversationId = group.Key,
                ExecutionCount = group.Select(record => record.TaskId).Distinct().Count(),
                MessageCount = group.Count(record =>
                    record.ConversationPayload != null && record.ConversationSequence != null
                ),
            })
            .ToDictionaryAsync(item => item.ConversationId, cancellationToken);
        var statusRecords = await histories
            .Where(record => conversationIds.Contains(record.ConversationId))
            .Select(record => new ProjectConversationChatHistory
            {
                Id = record.Id,
                ConversationId = record.ConversationId,
                TaskId = record.TaskId,
                JobId = record.JobId,
                Status = record.Status,
                FinishedTime = record.FinishedTime,
                TaskErrorMessage = record.TaskErrorMessage,
                Error = record.Error,
                CreateTime = record.CreateTime,
                UpdateTime = record.UpdateTime,
            })
            .ToListAsync(cancellationToken);
        var recordsByConversationId = statusRecords
            .GroupBy(record => record.ConversationId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ProjectConversationChatHistory>)group.ToList());

        var items = conversations
            .Select(conversation =>
                ToSummaryResponse(
                    conversation,
                    aggregates.GetValueOrDefault(conversation.Id),
                    recordsByConversationId.GetValueOrDefault(conversation.Id) ?? []
                )
            )
            .ToList();
        return CreateConversationPage(items, total, query);
    }

    public async Task<ProjectConversationResponse?> GetResponseAsync(
        Guid projectId,
        Guid conversationId,
        CancellationToken cancellationToken = default
    )
    {
        if (conversationId == Guid.Empty)
        {
            return null;
        }

        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return null;
        }

        var conversation = await _dbContext.ProjectConversations.SingleOrDefaultAsync(item =>
            item.ProjectId == project.Id && item.Id == conversationId && item.CreateBy == project.CreateBy
        );

        return conversation == null ? null : await ToResponseAsync(conversation, cancellationToken);
    }

    public async Task<ProjectConversationMessagePageResponse?> GetMessagePageAsync(
        Guid projectId,
        Guid conversationId,
        ProjectConversationMessagesQuery query,
        CancellationToken cancellationToken = default
    )
    {
        ValidateMessagePageQuery(query);
        var cursor = DecodeCursor(query.Cursor);

        var conversation = await GetProjectConversationAsync(projectId, conversationId);
        if (conversation == null)
        {
            return null;
        }

        var recordsQuery = _dbContext
            .ProjectConversationChatHistories.AsNoTracking()
            .Where(record =>
                record.ConversationId == conversation.Id
                && record.ConversationPayload != null
                && record.ConversationSequence != null
            );

        if (cursor.HasValue)
        {
            recordsQuery =
                query.Direction == ProjectConversationMessageDirection.Newer
                    ? recordsQuery.Where(record => record.ConversationSequence > cursor.Value)
                    : recordsQuery.Where(record => record.ConversationSequence < cursor.Value);
        }

        var records =
            query.Direction == ProjectConversationMessageDirection.Newer
                ? await recordsQuery
                    .OrderBy(record => record.ConversationSequence)
                    .ThenBy(record => record.Id)
                    .Take(query.PageSize + 1)
                    .ToListAsync(cancellationToken)
                : await recordsQuery
                    .OrderByDescending(record => record.ConversationSequence)
                    .ThenByDescending(record => record.Id)
                    .Take(query.PageSize + 1)
                    .ToListAsync(cancellationToken);

        var hasMore = records.Count > query.PageSize;
        if (hasMore)
        {
            records.RemoveAt(records.Count - 1);
        }

        if (query.Direction == ProjectConversationMessageDirection.Older)
        {
            records.Reverse();
        }

        var messages = records.SelectMany(TaskExecutionMapper.ToAiMessages).ToList();
        var cursorSequence =
            records.Count == 0 ? null
            : query.Direction == ProjectConversationMessageDirection.Newer ? records[^1].ConversationSequence
            : records[0].ConversationSequence;

        return new ProjectConversationMessagePageResponse(
            messages,
            hasMore && cursorSequence.HasValue ? EncodeCursor(cursorSequence.Value) : null,
            hasMore
        );
    }

    public async Task<ApplicationResult> ClearRecordsAsync(Guid projectId, Guid conversationId)
    {
        var conversation = await GetProjectConversationAsync(projectId, conversationId);
        if (conversation == null)
        {
            return ApplicationResult.NotFound();
        }

        var cleared = await _deletionCoordinator.ClearConversationRecordsAsync(ToDeletionTarget(conversation));
        return cleared ? ApplicationResult.Success() : ApplicationResult.NotFound();
    }

    public async Task<ApplicationResult> UpdateTitleAsync(
        Guid projectId,
        Guid conversationId,
        string title,
        string user
    )
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return ApplicationResult.Invalid("title is required.");
        }

        var conversation = await GetProjectConversationAsync(projectId, conversationId);
        if (conversation == null)
        {
            return ApplicationResult.NotFound();
        }

        conversation.Title = title.Trim();
        conversation.UpdateBy = user;
        conversation.UpdateTime = _timeProvider.GetUtcNow();
        _dbContext.ProjectConversations.Entry(conversation).Property(item => item.Title).IsModified = true;
        await _dbContext.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<ApplicationResult> DeleteAllAsync(Guid projectId)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return ApplicationResult.NotFound();
        }

        var deleted = await _deletionCoordinator.DeleteAllConversationsAsync(
            new ProjectDeletionTarget(project.Id, project.CreateBy!)
        );
        return deleted ? ApplicationResult.Success() : ApplicationResult.NotFound();
    }

    public async Task<bool> DeleteAsync(Guid projectId, Guid conversationId)
    {
        var conversation = await GetProjectConversationAsync(projectId, conversationId);
        if (conversation == null)
        {
            return false;
        }

        return await _deletionCoordinator.DeleteConversationAsync(ToDeletionTarget(conversation));
    }

    private static ProjectConversationDeletionTarget ToDeletionTarget(ProjectConversation conversation) =>
        new(conversation.ProjectId, conversation.Id, conversation.ContextId, conversation.CreateBy!);

    private async Task<ProjectConversationResponse> ToResponseAsync(
        ProjectConversation conversation,
        CancellationToken cancellationToken
    )
    {
        var recordsQuery = _dbContext
            .ProjectConversationChatHistories.AsNoTracking()
            .Where(record => record.ConversationId == conversation.Id);
        var executionCount = await recordsQuery
            .Select(record => record.TaskId)
            .Distinct()
            .CountAsync(cancellationToken);
        var messageCount = await recordsQuery.CountAsync(
            record => record.ConversationPayload != null && record.ConversationSequence != null,
            cancellationToken
        );
        var latestRecord = await GetLatestRecordAsync(recordsQuery, cancellationToken);
        var usage = await GetUsageAsync(conversation, cancellationToken);
        var resumeState = await GetResumeStateAsync(recordsQuery, cancellationToken);

        return new ProjectConversationResponse(
            conversation.ProjectId.Normalize(),
            conversation.Id,
            conversation.ContextId,
            conversation.JobId,
            conversation.Title,
            latestRecord?.Status,
            executionCount,
            messageCount,
            conversation.CreateTime,
            conversation.UpdateTime,
            latestRecord?.TaskErrorMessage ?? latestRecord?.Error,
            usage,
            resumeState
        );
    }

    private async Task<ProjectConversationUsage> GetUsageAsync(
        ProjectConversation conversation,
        CancellationToken cancellationToken
    ) =>
        await _dbContext
            .AgentUsages.Where(usage =>
                usage.ProjectId == conversation.ProjectId && usage.ContextId == conversation.ContextId
            )
            .GroupBy(_ => 1)
            .Select(group => new ProjectConversationUsage
            {
                InputTokenCount = group.Sum(usage => usage.InputTokenCount),
                OutputTokenCount = group.Sum(usage => usage.OutputTokenCount),
                TotalTokenCount = group.Sum(usage => usage.TotalTokenCount),
                CachedInputTokenCount = group.Sum(usage => usage.CachedInputTokenCount),
                ReasoningTokenCount = group.Sum(usage => usage.ReasoningTokenCount),
            })
            .SingleOrDefaultAsync(cancellationToken)
        ?? new ProjectConversationUsage();

    private static ProjectConversationSummaryResponse ToSummaryResponse(
        ConversationListRow conversation,
        ConversationListAggregate? aggregate,
        IReadOnlyList<ProjectConversationChatHistory> records
    )
    {
        var mappingContext = new ProjectConversation
        {
            Id = conversation.Id,
            Generation = conversation.Generation,
            ProjectId = conversation.ProjectId,
            ContextId = conversation.ContextId,
            JobId = conversation.JobId,
            Title = conversation.Title,
            CreateTime = conversation.CreateTime,
            UpdateTime = conversation.UpdateTime,
        };
        var tasks = records
            .GroupBy(record => record.TaskId)
            .Select(group => TaskExecutionMapper.ToTask(mappingContext, group.ToList()))
            .ToList();
        var latestTask = GetLatestTask(tasks);

        return new ProjectConversationSummaryResponse(
            conversation.ProjectId.Normalize(),
            conversation.Id,
            conversation.ContextId,
            conversation.JobId,
            conversation.Title,
            latestTask?.Status,
            aggregate?.ExecutionCount ?? 0,
            aggregate?.MessageCount ?? 0,
            conversation.CreateTime,
            conversation.UpdateTime,
            latestTask?.ErrorMessage
        );
    }

    private static async Task<ProjectConversationChatHistory?> GetLatestRecordAsync(
        IQueryable<ProjectConversationChatHistory> recordsQuery,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await recordsQuery
                .OrderByDescending(record => record.UpdateTime ?? record.CreateTime)
                .ThenByDescending(record => record.CreateTime)
                .ThenByDescending(record => record.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (NotSupportedException exception)
            when (exception.Message.Contains(
                    "SQLite does not support expressions of type 'DateTimeOffset'",
                    StringComparison.Ordinal
                )
            )
        {
            var records = await recordsQuery
                .Select(record => new ProjectConversationChatHistory
                {
                    Id = record.Id,
                    Status = record.Status,
                    TaskErrorMessage = record.TaskErrorMessage,
                    Error = record.Error,
                    CreateTime = record.CreateTime,
                    UpdateTime = record.UpdateTime,
                })
                .ToListAsync(cancellationToken);

            return records
                .OrderByDescending(record => record.UpdateTime ?? record.CreateTime)
                .ThenByDescending(record => record.CreateTime)
                .ThenByDescending(record => record.Id)
                .FirstOrDefault();
        }
    }

    private static async Task<ProjectConversationResumeStateResponse?> GetResumeStateAsync(
        IQueryable<ProjectConversationChatHistory> recordsQuery,
        CancellationToken cancellationToken
    )
    {
        var (targetType, targetId) = await GetLatestTargetAsync(recordsQuery, cancellationToken);
        var agentMode = await GetLatestAgentModeAsync(recordsQuery, cancellationToken);

        if (targetType == null && targetId == null && agentMode == null)
        {
            return null;
        }

        return new ProjectConversationResumeStateResponse(targetType, targetId, agentMode);
    }

    private static async Task<(string? TargetType, string? TargetId)> GetLatestTargetAsync(
        IQueryable<ProjectConversationChatHistory> recordsQuery,
        CancellationToken cancellationToken
    )
    {
        await foreach (
            var metadata in recordsQuery
                .Where(record => record.Metadata != null)
                .OrderByDescending(record => record.ConversationSequence)
                .Select(record => record.Metadata)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
        )
        {
            var targetType = GetMetadataString(metadata, "targetType");
            var targetId = GetMetadataString(metadata, "targetId");
            if (targetType != null && targetId != null)
            {
                return (targetType, targetId);
            }
        }

        return (null, null);
    }

    private static async Task<string?> GetLatestAgentModeAsync(
        IQueryable<ProjectConversationChatHistory> recordsQuery,
        CancellationToken cancellationToken
    )
    {
        await foreach (
            var metadata in recordsQuery
                .Where(record => record.Metadata != null)
                .OrderByDescending(record => record.ConversationSequence)
                .Select(record => record.Metadata)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
        )
        {
            var mode = NormalizeAgentMode(GetMetadataString(metadata, "agentMode"));
            if (mode != null)
            {
                return mode;
            }
        }

        await foreach (
            var record in recordsQuery
                .Where(record => record.ConversationPayload != null && record.ConversationPayload.Contains("\"mode\""))
                .OrderByDescending(record => record.ConversationSequence)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
        )
        {
            var message = record.ToChatMessage();
            if (message?.AdditionalProperties?.TryGetValue("mode", out var mode) == true)
            {
                var normalizedMode = NormalizeAgentMode(mode?.ToString());
                if (normalizedMode != null)
                {
                    return normalizedMode;
                }
            }
        }

        return null;
    }

    private static string? NormalizeAgentMode(string? mode) =>
        mode switch
        {
            "plan" => "plan",
            "execute" => "execute",
            _ => null,
        };

    private static string? GetMetadataString(Dictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata?.TryGetValue(key, out var value) != true)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static void ValidateMessagePageQuery(ProjectConversationMessagesQuery query)
    {
        if (query.PageSize is < 1 or > 100)
        {
            throw new AgwException(ErrorCodes.InvalidPageSize, "pageSize must be between 1 and 100.");
        }

        if (!Enum.IsDefined(query.Direction))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "direction must be 'newer' or 'older'.");
        }
    }

    private static string EncodeCursor(long sequence)
    {
        var bytes = Encoding.UTF8.GetBytes(sequence.ToString(CultureInfo.InvariantCulture));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static long? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var encoded = cursor.Trim().Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence))
            {
                return sequence;
            }
        }
        catch (FormatException)
        {
            // Fall through to the shared application error below.
        }

        throw new AgwException(ErrorCodes.InvalidParam, "cursor is invalid.");
    }

    private async Task<ProjectConversation?> GetProjectConversationAsync(Guid projectId, Guid conversationId)
    {
        if (conversationId == Guid.Empty)
        {
            return null;
        }

        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return null;
        }

        return await _dbContext.ProjectConversations.SingleOrDefaultAsync(conversation =>
            conversation.ProjectId == project.Id
            && conversation.Id == conversationId
            && conversation.CreateBy == project.CreateBy
        );
    }

    private static TaskProjection? GetLatestTask(IEnumerable<TaskProjection> tasks) =>
        tasks
            .OrderByDescending(task => task.UpdateTime ?? task.CreateTime)
            .ThenByDescending(task => task.CreateTime)
            .ThenByDescending(task => task.TaskId)
            .FirstOrDefault();

    private static void ValidateConversationListQuery(ProjectConversationListQuery query)
    {
        if (query.PageIndex < 1)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "pageIndex must be at least 1.");
        }

        if (query.PageSize is not (10 or 20 or 50))
        {
            throw new AgwException(ErrorCodes.InvalidPageSize, "pageSize must be one of 10, 20, or 50.");
        }
    }

    private static async Task<IReadOnlyList<ConversationListRow>> GetConversationPageAsync(
        IQueryable<ProjectConversation> query,
        ProjectConversationListQuery page,
        CancellationToken cancellationToken
    )
    {
        var skip = (long)(page.PageIndex - 1) * page.PageSize;
        if (skip > int.MaxValue)
        {
            return [];
        }

        try
        {
            return await SelectConversationListRows(
                    query
                        .OrderByDescending(conversation => conversation.UpdateTime ?? conversation.CreateTime)
                        .ThenByDescending(conversation => conversation.Id)
                        .Skip((int)skip)
                        .Take(page.PageSize)
                )
                .ToListAsync(cancellationToken);
        }
        catch (NotSupportedException exception)
            when (exception.Message.Contains(
                    "SQLite does not support expressions of type 'DateTimeOffset'",
                    StringComparison.Ordinal
                )
            )
        {
            var rows = await SelectConversationListRows(query).ToListAsync(cancellationToken);
            return rows.OrderByDescending(conversation => conversation.UpdateTime ?? conversation.CreateTime)
                .ThenByDescending(conversation => conversation.Id)
                .Skip((int)skip)
                .Take(page.PageSize)
                .ToList();
        }
    }

    private static IQueryable<ConversationListRow> SelectConversationListRows(IQueryable<ProjectConversation> query) =>
        query.Select(conversation => new ConversationListRow
        {
            Id = conversation.Id,
            Generation = conversation.Generation,
            ProjectId = conversation.ProjectId,
            JobId = conversation.JobId,
            ContextId = conversation.ContextId,
            Title = conversation.Title,
            CreateTime = conversation.CreateTime,
            UpdateTime = conversation.UpdateTime,
        });

    private static PagedResult<ProjectConversationSummaryResponse> CreateConversationPage(
        IReadOnlyList<ProjectConversationSummaryResponse> items,
        long total,
        ProjectConversationListQuery query
    ) =>
        new()
        {
            Items = items,
            Total = total,
            PageIndex = query.PageIndex,
            PageSize = query.PageSize,
        };

    private sealed class ConversationListRow
    {
        public Guid Id { get; init; }
        public int Generation { get; init; }
        public Guid ProjectId { get; init; }
        public Guid? JobId { get; init; }
        public string ContextId { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public DateTimeOffset CreateTime { get; init; }
        public DateTimeOffset? UpdateTime { get; init; }
    }

    private sealed class ConversationListAggregate
    {
        public Guid ConversationId { get; init; }
        public int ExecutionCount { get; init; }
        public int MessageCount { get; init; }
    }
}
