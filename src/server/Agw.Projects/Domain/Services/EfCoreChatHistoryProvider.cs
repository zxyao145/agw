using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Agw.Projects.Domain.Services;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Utils;

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
public sealed class EfCoreChatHistoryProvider : ChatHistoryProvider, IProviderSessionState, IConversationHistoryWriter
{
    private static readonly JsonSerializerOptions DefaultJsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
        ChatHistoryProviderStateJsonContext.Default,
        new DefaultJsonTypeInfoResolver())
    };

    private const string DefaultUser = "system";
    private const string HistoryScopeMetadataKey = "historyScope";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<EfCoreChatHistoryProvider> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly ProviderSessionState<State> _state;

    /// <summary>
    /// 创建 EF Core 聊天历史提供器，并配置默认的规范化 context 会话状态。
    /// </summary>
    public EfCoreChatHistoryProvider(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<EfCoreChatHistoryProvider> logger,
        TimeProvider timeProvider,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _timeProvider = timeProvider;
        _jsonSerializerOptions = jsonSerializerOptions ?? DefaultJsonSerializerOptions;
        _state = new ProviderSessionState<State>(
            _ =>
            {
                var contextId = ContextIdUtil.ResolveContextId(null);
                return new State(contextId, ProjectDefaults.DefaultBuiltInId);
            },
            nameof(EfCoreChatHistoryProvider),
            _jsonSerializerOptions);
    }

    /// <summary>
    /// 使用规范化 context ID 和项目标识初始化 Agent 会话状态。
    /// </summary>
    public void InitializeSessionState(
        AgentSession session,
        string contextId,
        Guid projectId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var state = new State(
            ContextIdUtil.NormalizeContextId(contextId),
            projectId);
        _state.SaveState(session, state);
    }

    public void InitializeSessionState(
        AgentSession session,
        string contextId,
        Guid projectId,
        string historyScope)
    {
        ArgumentNullException.ThrowIfNull(session);
        var state = new State(
            ContextIdUtil.NormalizeContextId(contextId),
            projectId,
            historyScope.Trim());
        _state.SaveState(session, state);
    }

    public bool TryGetProjectContext(AgentSession session, out Guid projectId, out string contextId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.StateBag.TryGetValue(_state.StateKey, out State? state, _jsonSerializerOptions) || state == null)
        {
            projectId = Guid.Empty;
            contextId = string.Empty;
            return false;
        }

        projectId = state.ProjectId;
        contextId = state.ContextId;
        return true;
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

        var records = await dbContext.Set<TaskRecord>()
            .AsNoTracking()
            .Where(record => record.ProjectContextId == projectContext.Id
                && record.ConversationPayload != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var payloads = records
            .Where(record => HasHistoryScope(record, state.HistoryScope))
            .OrderBy(record => record.ConversationSequence ?? long.MinValue)
            .ThenBy(record => record.CreateTime)
            .ThenBy(record => record.Id)
            .Select(record => record.ConversationPayload!)
            .ToList();

        var messages = new List<ChatMessage>(payloads.Count);
        foreach (var payload in payloads)
        {
            var message = JsonSerializer.Deserialize<ChatMessage>(payload, _jsonSerializerOptions);
            if (message == null)
            {
                _logger.LogWarning("Skipping null chat history message for context {ContextId}.", state.ContextId);
                continue;
            }

            if (IsResult(message))
            {
                continue;
            }

            messages.Add(message);
        }

        // 旧会话可能残留没有对应响应的工具审批请求，FunctionInvokingChatClient 会在下一轮重放历史时直接抛错。
        // 响应也可能随本轮输入提交，因此合并检查后只过滤仍未答复的请求。
        var approvalResponseCallIds = messages
            .Concat(context.RequestMessages)
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalResponseContent>()
            .Select(content => content.ToolCall)
            .OfType<FunctionCallContent>()
            .Select(content => content.CallId)
            .ToHashSet(StringComparer.Ordinal);

        var filteredMessages = messages
            .Select(message => RemoveUnansweredToolApprovalRequests(message, approvalResponseCallIds))
            .OfType<ChatMessage>()
            .ToList();
        return RemoveOrphanedFunctionResults(filteredMessages);
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
        await AppendAsync(
            state.ProjectId,
            state.ContextId,
            newMessages,
            state.HistoryScope,
            cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 将消息追加到规范化 context，并复用及修正 SQLite 中 GUID 大小写不同的旧上下文记录。
    /// </summary>
    public async Task AppendAsync(
        Guid projectId,
        string contextId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        await AppendAsync(
            projectId,
            contextId,
            messages,
            historyScope: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendAsync(
        Guid projectId,
        string contextId,
        IReadOnlyList<ChatMessage> messages,
        string? historyScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var persistableMessages = messages.Where(HasContent).ToList();
        if (persistableMessages.Count == 0)
        {
            return;
        }

        contextId = ContextIdUtil.NormalizeContextId(contextId);

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();

        var now = _timeProvider.GetUtcNow();
        var firstUserText = ExtractFirstText(persistableMessages.FirstOrDefault(message => message.Role == ChatRole.User));
        var projectContext = await dbContext.Set<ProjectContext>()
            .SingleOrDefaultAsync(
                x => x.ProjectId == projectId && x.ContextId == contextId,
                cancellationToken)
            .ConfigureAwait(false);
        if (projectContext == null && Guid.TryParse(contextId, out _))
        {
            projectContext = await dbContext.Set<ProjectContext>()
                .Where(x => x.ProjectId == projectId && x.ContextId.ToLower() == contextId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (projectContext != null)
            {
                projectContext.ContextId = contextId;
            }
        }

        if (projectContext == null)
        {
            projectContext = new ProjectContext
            {
                Id = Guid.CreateVersion7(),
                ProjectId = projectId,
                ContextId = contextId,
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

        var taskId = Guid.CreateVersion7();

        var nextSequence = await dbContext.Set<TaskRecord>()
            .Where(x => x.ProjectContextId == projectContext.Id)
            .Select(x => x.ConversationSequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? -1;

        foreach (ChatMessage message in persistableMessages)
        {
            // user input
            nextSequence++;

            dbContext.Set<TaskRecord>().Add(new TaskRecord
            {
                Id = Guid.CreateVersion7(),
                ProjectContextId = projectContext.Id,
                TaskId = taskId,
                Status = TaskExecutionStatus.Succeeded,
                AgentName = message.AuthorName,
                ConversationSequence = nextSequence,
                ConversationPayload = JsonSerializer.Serialize(message, _jsonSerializerOptions),
                Metadata = CreateMetadata(message, historyScope),
                CreateTime = now,
                UpdateTime = now
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsResult(ChatMessage message) =>
        message.AdditionalProperties?.TryGetValue("type", out var type) == true &&
        string.Equals(type?.ToString(), "result", StringComparison.Ordinal);

    private static bool HasContent(ChatMessage message) =>
        message.Contents.Any(content =>
            content is not TextContent textContent ||
            !string.IsNullOrWhiteSpace(textContent.Text));

    private static bool HasHistoryScope(TaskRecord record, string? historyScope)
    {
        string? recordScope = null;
        if (record.Metadata?.TryGetValue(HistoryScopeMetadataKey, out var scopeElement) == true &&
            scopeElement.ValueKind == JsonValueKind.String)
        {
            recordScope = scopeElement.GetString();
        }

        return string.Equals(recordScope, historyScope, StringComparison.Ordinal);
    }

    private static Dictionary<string, JsonElement>? CreateMetadata(
        ChatMessage message,
        string? historyScope)
    {
        var metadata = TaskRecordMetadataFactory.FromMessage(message);
        if (historyScope == null)
        {
            return metadata;
        }

        metadata ??= [];
        metadata[HistoryScopeMetadataKey] = JsonSerializer.SerializeToElement(historyScope);
        return metadata;
    }

    private static List<ChatMessage> RemoveOrphanedFunctionResults(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        var pendingCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Assistant)
            {
                pendingCallIds.Clear();
                pendingCallIds.UnionWith(message.Contents
                    .OfType<FunctionCallContent>()
                    .Select(content => content.CallId));
                result.Add(message);
                continue;
            }

            if (message.Role != ChatRole.Tool)
            {
                pendingCallIds.Clear();
                result.Add(message);
                continue;
            }

            var contents = message.Contents
                .Where(content =>
                    content is not FunctionResultContent functionResult ||
                    pendingCallIds.Remove(functionResult.CallId))
                .ToList();
            if (contents.Count == 0)
            {
                continue;
            }

            if (contents.Count == message.Contents.Count)
            {
                result.Add(message);
                continue;
            }

            var filteredMessage = message.Clone();
            filteredMessage.Contents = contents;
            result.Add(filteredMessage);
        }

        return result;
    }

    private static ChatMessage? RemoveUnansweredToolApprovalRequests(
        ChatMessage message,
        IReadOnlySet<string> approvalResponseCallIds)
    {
        var contents = message.Contents
            .Where(content =>
                content is not ToolApprovalRequestContent
                {
                    ToolCall: FunctionCallContent { InformationalOnly: false } functionCall
                }
                || approvalResponseCallIds.Contains(functionCall.CallId))
            .ToList();
        if (contents.Count == message.Contents.Count)
        {
            return message;
        }

        if (contents.Count == 0)
        {
            return null;
        }

        var filteredMessage = message.Clone();
        filteredMessage.Contents = contents;
        return filteredMessage;
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

    public sealed record State
    {
        public string ContextId { get; init; }

        public Guid ProjectId { get; init; }

        public string? HistoryScope { get; init; }

        public State(string contextId, Guid projectId, string? historyScope = null)
        {
            ContextId = contextId;
            ProjectId = projectId;
            HistoryScope = historyScope;
        }
    }
}
