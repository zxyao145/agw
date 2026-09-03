using System.Globalization;
using System.Text;
using System.Text.Json;
using Agw.Projects.Domain.Services;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public class ProjectConversationAppService
{
    private readonly IRepository<ProjectConversation> _conversationRepository;
    private readonly IRepository<ProjectConversationChatHistory> _recordRepository;
    private readonly IRepository<AgentflowCheckpointRecord> _checkpointRepository;
    private readonly IRepository<AgentflowTrace> _traceRepository;
    private readonly IRepository<AgentUsage> _usageRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ProjectResolver _projectResolver;
    private readonly ITaskSessionBindingService _taskSessionBindingService;
    private readonly TimeProvider _timeProvider;

    public ProjectConversationAppService(
        IRepository<ProjectConversation> conversationRepository,
        IRepository<ProjectConversationChatHistory> recordRepository,
        IRepository<AgentflowCheckpointRecord> checkpointRepository,
        IRepository<AgentflowTrace> traceRepository,
        IRepository<AgentUsage> usageRepository,
        IUnitOfWork unitOfWork,
        ProjectResolver projectResolver,
        ITaskSessionBindingService taskSessionBindingService,
        TimeProvider timeProvider
    )
    {
        _conversationRepository = conversationRepository;
        _recordRepository = recordRepository;
        _checkpointRepository = checkpointRepository;
        _traceRepository = traceRepository;
        _usageRepository = usageRepository;
        _unitOfWork = unitOfWork;
        _projectResolver = projectResolver;
        _taskSessionBindingService = taskSessionBindingService;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<ProjectConversationSummaryResponse>> ListResponsesAsync(Guid projectId)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return [];
        }

        var conversations = await _conversationRepository.ListAsync(conversation =>
            conversation.ProjectId == project.Id && conversation.CreateBy == project.CreateBy
        );
        if (conversations.Count == 0)
        {
            return [];
        }

        var conversationIds = conversations.Select(conversation => conversation.Id).ToHashSet();
        var records = await _recordRepository.ListAsync(record => conversationIds.Contains(record.ConversationId));
        var recordsByConversationId = records
            .GroupBy(record => record.ConversationId)
            .ToDictionary(group => group.Key, group => group.ToList());

        return conversations
            .Select(conversation =>
                ToSummaryResponse(conversation, recordsByConversationId.GetValueOrDefault(conversation.Id) ?? [])
            )
            .Where(ShouldIncludeConversation)
            .OrderByDescending(conversation => conversation.UpdateTime ?? conversation.CreateTime)
            .ToList();
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

        var conversation = await _conversationRepository.SingleOrDefaultAsync(item =>
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

        var recordsQuery = _recordRepository
            .Queryable.AsNoTracking()
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

        await _recordRepository
            .Queryable.Where(record => record.ConversationId == conversation.Id)
            .ExecuteDeleteAsync();
        await _checkpointRepository
            .Queryable.Where(checkpoint => checkpoint.ProjectConversationId == conversation.Id)
            .ExecuteDeleteAsync();
        await _traceRepository
            .Queryable.Where(trace =>
                trace.ProjectId == conversation.ProjectId && trace.ContextId == conversation.ContextId
            )
            .ExecuteDeleteAsync();

        await _taskSessionBindingService.DeleteByConversationAsync(conversation.Id);

        await _unitOfWork.SaveChangesAsync();
        return ApplicationResult.Success();
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
        _conversationRepository.Update(conversation);
        await _unitOfWork.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<ApplicationResult> DeleteAllAsync(Guid projectId)
    {
        var project = await _projectResolver.ResolveRequiredAsync(projectId);
        if (project == null)
        {
            return ApplicationResult.NotFound();
        }

        var conversations = await _conversationRepository.ListAsync(conversation =>
            conversation.ProjectId == project.Id && conversation.CreateBy == project.CreateBy
        );
        foreach (var conversation in conversations)
        {
            await _taskSessionBindingService.DeleteByConversationAsync(conversation.Id);
        }

        var conversationIds = conversations.Select(conversation => conversation.Id).ToArray();
        if (conversationIds.Length > 0)
        {
            await _recordRepository
                .Queryable.Where(record => conversationIds.Contains(record.ConversationId))
                .ExecuteDeleteAsync();
        }

        await _traceRepository.Queryable.Where(trace => trace.ProjectId == project.Id).ExecuteDeleteAsync();
        await _checkpointRepository
            .Queryable.Where(checkpoint => checkpoint.ProjectId == project.Id)
            .ExecuteDeleteAsync();

        await _conversationRepository
            .Queryable.Where(conversation =>
                conversation.ProjectId == project.Id && conversation.CreateBy == project.CreateBy
            )
            .ExecuteDeleteAsync();

        await _unitOfWork.SaveChangesAsync();
        return ApplicationResult.Success();
    }

    public async Task<bool> DeleteAsync(Guid projectId, Guid conversationId)
    {
        var conversation = await GetProjectConversationAsync(projectId, conversationId);
        if (conversation == null)
        {
            return false;
        }

        await _recordRepository
            .Queryable.Where(record => record.ConversationId == conversation.Id)
            .ExecuteDeleteAsync();
        await _checkpointRepository
            .Queryable.Where(checkpoint => checkpoint.ProjectConversationId == conversation.Id)
            .ExecuteDeleteAsync();
        await _traceRepository
            .Queryable.Where(trace =>
                trace.ProjectId == conversation.ProjectId && trace.ContextId == conversation.ContextId
            )
            .ExecuteDeleteAsync();

        await _taskSessionBindingService.DeleteByConversationAsync(conversation.Id);

        _conversationRepository.Remove(conversation);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    private async Task<ProjectConversationResponse> ToResponseAsync(
        ProjectConversation conversation,
        CancellationToken cancellationToken
    )
    {
        var recordsQuery = _recordRepository
            .Queryable.AsNoTracking()
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
        await _usageRepository
            .Queryable.Where(usage =>
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
        ProjectConversation conversation,
        IReadOnlyList<ProjectConversationChatHistory> records
    )
    {
        var tasks = records
            .GroupBy(record => record.TaskId)
            .Select(group => TaskExecutionMapper.ToTask(conversation, group.ToList()))
            .ToList();
        var latestTask = GetLatestTask(tasks);
        var messageCount = records.Count(record => record.ToChatMessage() != null);

        return new ProjectConversationSummaryResponse(
            conversation.ProjectId.Normalize(),
            conversation.Id,
            conversation.ContextId,
            conversation.JobId,
            conversation.Title,
            latestTask?.Status,
            tasks.Count,
            messageCount,
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

        return await _conversationRepository.SingleOrDefaultAsync(conversation =>
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

    private static bool ShouldIncludeConversation(ProjectConversationSummaryResponse conversation) =>
        conversation.JobId == null || conversation.MessageCount > 0 || conversation.ExecutionCount == 0;
}
