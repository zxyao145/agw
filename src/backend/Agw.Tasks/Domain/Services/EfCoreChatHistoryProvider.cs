using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;
using Agw.Tasks.Domain.Services;

using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Domain.Services;

[JsonSerializable(typeof(EfCoreChatHistoryProvider.State))]
internal partial class ChatHistoryProviderStateJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Persists agent chat history in EF Core while keeping the conversation key in session state.
/// </summary>
public sealed class EfCoreChatHistoryProvider : ChatHistoryProvider, IProviderSessionState
{
    private static readonly JsonSerializerOptions DefaultJsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
        ChatHistoryProviderStateJsonContext.Default,
        new DefaultJsonTypeInfoResolver())
    };

    private const string DefaultUser = "system";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<EfCoreChatHistoryProvider> _logger;
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly ProviderSessionState<State> _state;

    public EfCoreChatHistoryProvider(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<EfCoreChatHistoryProvider> logger,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _jsonSerializerOptions = jsonSerializerOptions ?? DefaultJsonSerializerOptions;
        _state = new ProviderSessionState<State>(
            _ =>
            {
                var contextId = TaskUtil.GenContextId();
                var taskId = Guid.NewGuid().Normalize();
                return new State(contextId, taskId, ProjectDefaults.DefaultBuiltInId);
            },
            nameof(EfCoreChatHistoryProvider),
            _jsonSerializerOptions);
    }

    public void InitializeSessionState(
        AgentSession session,
        string contextId,
        string? taskId,
        Guid projectId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);
        if (Guid.TryParse(contextId, out var guidContextId))
        {
            contextId = guidContextId.Normalize();
        }

        var state = new State(
            contextId.Trim(),
            string.IsNullOrWhiteSpace(taskId) ? contextId.Trim() : taskId.Trim(),
            projectId);
        _state.SaveState(session, state);
    }

    /// <summary>
    /// 获取历史消息
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var state = _state.GetOrInitializeState(context.Session);
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();

        var projectContext = await dbContext.Set<ProjectContext>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                context => context.ProjectId == state.ProjectId && context.ContextId == state.ContextId,
                cancellationToken)
            .ConfigureAwait(false);
        if (projectContext == null)
        {
            return [];
        }

        var payloads = await dbContext.Set<TaskRecord>()
            .AsNoTracking()
            .Where(record => record.ProjectContextId == projectContext.Id
                && record.ConversationPayload != null)
            .OrderBy(record => record.ConversationSequence ?? long.MinValue)
            .ThenBy(record => record.CreateTime)
            .ThenBy(record => record.Id)
            .Select(record => record.ConversationPayload!)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var messages = new List<ChatMessage>(payloads.Count);
        foreach (var payload in payloads)
        {
            var message = JsonSerializer.Deserialize<ChatMessage>(payload, _jsonSerializerOptions);
            if (message == null)
            {
                _logger.LogWarning("Skipping null chat history message for context {ContextId}.", state.ContextId);
                continue;
            }

            messages.Add(message);
        }

        return messages;
    }

    /// <summary>
    /// 存储新消息
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="ArgumentNullException"></exception>
    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var newMessages = context.RequestMessages
            .Concat(context.ResponseMessages ?? [])
            .ToList();
        if (newMessages.Count == 0)
        {
            return;
        }

        var state = _state.GetOrInitializeState(context.Session);
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();

        var now = DateTime.UtcNow;
        var firstUserText = ExtractFirstText(newMessages.FirstOrDefault(message => message.Role == ChatRole.User));
        var projectContext = await dbContext.Set<ProjectContext>()
            .SingleOrDefaultAsync(
                x => x.ProjectId == state.ProjectId && x.ContextId == state.ContextId,
                cancellationToken)
            .ConfigureAwait(false);

        if (projectContext == null)
        {
            projectContext = new ProjectContext
            {
                Id = Guid.NewGuid(),
                ProjectId = state.ProjectId,
                ContextId = state.ContextId,
                Title = TaskTitleFactory.Create(firstUserText),
                CreateBy = DefaultUser,
                CreateTime = now,
                UpdateBy = DefaultUser,
                UpdateTime = now
            };
            dbContext.Set<ProjectContext>().Add(projectContext);
        }
        else
        {
            var titleFromUser = TaskTitleFactory.Create(firstUserText);
            if (
                string.Equals(projectContext.Title, TaskTitleFactory.DefaultTitle, StringComparison.Ordinal)
                && !string.Equals(titleFromUser, TaskTitleFactory.DefaultTitle, StringComparison.Ordinal))
            {
                projectContext.Title = titleFromUser;
            }

            projectContext.UpdateBy = DefaultUser;
            projectContext.UpdateTime = now;
        }

        var taskGuid = ParseSessionTaskId(state.TaskId) ?? Guid.NewGuid();
        state = state with { TaskId = taskGuid.Normalize() };

        var nextSequence = await dbContext.Set<TaskRecord>()
            .Where(x => x.ProjectContextId == projectContext.Id)
            .Select(x => x.ConversationSequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? -1;

        foreach (ChatMessage message in newMessages)
        {
            // user input
            nextSequence++;

            dbContext.Set<TaskRecord>().Add(new TaskRecord
            {
                Id = Guid.NewGuid(),
                ProjectContextId = projectContext.Id,
                TaskId = taskGuid,
                Status = TaskExecutionStatus.Succeeded,
                AgentName = message.AuthorName,
                ConversationSequence = nextSequence,
                ConversationPayload = JsonSerializer.Serialize(message, _jsonSerializerOptions),
                Metadata = TaskRecordMetadataFactory.FromMessage(message),
                CreateTime = now,
                UpdateTime = now
            });
        }

        _state.SaveState(context.Session, state);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task<Project?> ResolveProjectAsync(DbContext dbContext, string projectId, CancellationToken cancellationToken)
    {
        var normalizedProjectId = projectId.Trim();
        if (Guid.TryParse(normalizedProjectId, out var projectGuid))
        {
            return dbContext.Set<Project>()
                .SingleOrDefaultAsync(project => project.Id == projectGuid, cancellationToken);
        }

        return dbContext.Set<Project>()
            .SingleOrDefaultAsync(project => project.Name.ToLower() == normalizedProjectId.ToLower(), cancellationToken);
    }

    private static string? ExtractFirstText(ChatMessage? message)
    {
        if (message == null)
        {
            return null;
        }

        return string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text))
            .Trim();
    }

    private static Guid? ParseSessionTaskId(string taskIdString) =>
        Guid.TryParse(taskIdString, out var taskId) ? taskId : null;

    public sealed record State(string ContextId, string TaskId, Guid ProjectId);
}
