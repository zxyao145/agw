using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Agw.Auth.Contracts;
using Agw.Projects.Domain.Services;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Projects.Infrastructure;

[JsonSerializable(typeof(EfCoreChatHistoryProvider.State))]
internal partial class ChatHistoryProviderStateJsonContext : JsonSerializerContext { }

/// <summary>
/// Persists agent chat history in EF Core while keeping the conversation key in session state.
/// </summary>
public sealed class EfCoreChatHistoryProvider : ChatHistoryProvider, IProviderSessionState, IConversationHistoryWriter
{
    private static readonly JsonSerializerOptions DefaultJsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            ChatHistoryProviderStateJsonContext.Default,
            new DefaultJsonTypeInfoResolver()
        ),
    };

    private const string AgentNamePropertyName = "agentName";
    private const string HistoryScopeMetadataKey = "historyScope";
    private const string NodeNamePropertyName = "nodeName";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IApplicationLock _applicationLock;
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
        JsonSerializerOptions? jsonSerializerOptions = null
    )
        : this(serviceScopeFactory, InMemoryApplicationLock.Shared, logger, timeProvider, jsonSerializerOptions) { }

    public EfCoreChatHistoryProvider(
        IServiceScopeFactory serviceScopeFactory,
        IApplicationLock applicationLock,
        ILogger<EfCoreChatHistoryProvider> logger,
        TimeProvider timeProvider,
        JsonSerializerOptions? jsonSerializerOptions = null
    )
    {
        _serviceScopeFactory = serviceScopeFactory;
        _applicationLock = applicationLock;
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
            _jsonSerializerOptions
        );
    }

    /// <summary>
    /// 使用规范化 context ID 和项目标识初始化 Agent 会话状态。
    /// </summary>
    public void InitializeSessionState(AgentSession session, string contextId, Guid projectId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var state = new State(ContextIdUtil.NormalizeContextId(contextId), projectId);
        _state.SaveState(session, state);
    }

    public void InitializeSessionState(AgentSession session, string contextId, Guid projectId, string historyScope)
    {
        InitializeSessionState(session, contextId, projectId, historyScope, nodeName: null);
    }

    public void InitializeSessionState(
        AgentSession session,
        string contextId,
        Guid projectId,
        string historyScope,
        string? nodeName
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        var state = new State(ContextIdUtil.NormalizeContextId(contextId), projectId, historyScope.Trim(), nodeName);
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
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        var state = _state.GetOrInitializeState(context.Session);
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();

        var projectConversation = await dbContext
            .Set<ProjectConversation>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                context => context.ProjectId == state.ProjectId && context.ContextId == state.ContextId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (projectConversation == null)
        {
            return [];
        }

        var records = await dbContext
            .Set<ProjectConversationChatHistory>()
            .AsNoTracking()
            .Where(record => record.ConversationId == projectConversation.Id && record.ConversationPayload != null)
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

            if (IsExcludedFromModelHistory(message))
            {
                continue;
            }

            messages.Add(message);
        }

        // 旧会话可能残留没有对应响应的工具审批请求，FunctionInvokingChatClient 会在下一轮重放历史时直接抛错。
        // 响应也可能随本轮输入提交，因此合并检查后只过滤仍未答复的请求。
        var requestMessages = context.RequestMessages.ToList();
        var approvalResponseCallIds = messages
            .Concat(requestMessages)
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
        return RemoveIncompleteFunctionCallsAndOrphanedResults(filteredMessages, requestMessages);
    }

    /// <summary>
    /// 存储新消息
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="ArgumentNullException"></exception>
    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        var preludeMessages =
            context.Session == null ? Array.Empty<ChatMessage>() : ConversationHistoryPrelude.Take(context.Session);
        var state = _state.GetOrInitializeState(context.Session);
        var newMessages = context
            .RequestMessages.Concat(preludeMessages)
            .Concat(AddResponseMetadata(context.ResponseMessages ?? [], state.NodeName, context.Agent.Name))
            .ToList();
        if (newMessages.Count == 0)
        {
            return;
        }

        await AppendAsync(state.ProjectId, state.ContextId, newMessages, state.HistoryScope, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 将消息追加到规范化 context，并复用及修正 SQLite 中 GUID 大小写不同的旧上下文记录。
    /// </summary>
    public async Task AppendAsync(
        Guid projectId,
        string contextId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default
    )
    {
        await AppendAsync(projectId, contextId, messages, historyScope: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendAsync(
        Guid projectId,
        string contextId,
        IReadOnlyList<ChatMessage> messages,
        string? historyScope,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(messages);
        var persistableMessages = messages
            .Where(message => !ConversationHandoffMetadata.IsHandoffMessage(message))
            .Where(message => !ConversationHistoryMetadata.IsPersistenceExcluded(message))
            .Select(RemoveBlankTextualContent)
            .OfType<ChatMessage>()
            .ToList();
        if (persistableMessages.Count == 0)
        {
            return;
        }

        contextId = ContextIdUtil.NormalizeContextId(contextId);

        await using var lifecycleLease = await _applicationLock
            .AcquireAsync(ProjectLifecycleLock.GetResourceName(projectId), cancellationToken)
            .ConfigureAwait(false);
        await using var mutationLease = await _applicationLock
            .AcquireAsync($"conversation-history:{projectId:D}:{contextId}", cancellationToken)
            .ConfigureAwait(false);

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();

        if (!UserInfoUtil.IsContextActive)
        {
            throw new AgwException(ErrorCodes.AuthenticationRequired);
        }

        var ownerUserId = UserInfoUtil.RequiredUserId;
        if (
            !await dbContext
                .Set<Project>()
                .AnyAsync(project => project.Id == projectId && project.CreateBy == ownerUserId, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var firstUserText = ExtractFirstText(
            persistableMessages.FirstOrDefault(message => message.Role == ChatRole.User)
        );
        var projectConversation = await dbContext
            .Set<ProjectConversation>()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.ContextId == contextId, cancellationToken)
            .ConfigureAwait(false);
        if (projectConversation == null && Guid.TryParse(contextId, out _))
        {
            projectConversation = await dbContext
                .Set<ProjectConversation>()
                .Where(x => x.ProjectId == projectId && x.ContextId.ToLower() == contextId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (projectConversation != null)
            {
                projectConversation.ContextId = contextId;
            }
        }

        if (projectConversation == null)
        {
            projectConversation = new ProjectConversation
            {
                Id = Guid.CreateVersion7(),
                ProjectId = projectId,
                ContextId = contextId,
                Title = TaskTitleFactory.Create(firstUserText),
                CreateBy = ResolveCurrentUserId(),
                CreateTime = now,
                UpdateBy = ResolveCurrentUserId(),
                UpdateTime = now,
            };
            dbContext.Set<ProjectConversation>().Add(projectConversation);
        }
        else
        {
            var titleFromUser = TaskTitleFactory.Create(firstUserText);
            if (
                string.Equals(projectConversation.Title, TaskTitleFactory.DefaultTitle, StringComparison.Ordinal)
                && !string.Equals(titleFromUser, TaskTitleFactory.DefaultTitle, StringComparison.Ordinal)
            )
            {
                projectConversation.Title = titleFromUser;
            }

            projectConversation.UpdateBy = ResolveCurrentUserId();
            projectConversation.UpdateTime = now;
        }

        var taskId = Guid.CreateVersion7();

        var nextSequence =
            await dbContext
                .Set<ProjectConversationChatHistory>()
                .Where(x => x.ConversationId == projectConversation.Id)
                .Select(x => x.ConversationSequence)
                .MaxAsync(cancellationToken)
                .ConfigureAwait(false)
            ?? -1;

        foreach (ChatMessage message in persistableMessages)
        {
            // user input
            nextSequence++;

            dbContext
                .Set<ProjectConversationChatHistory>()
                .Add(
                    new ProjectConversationChatHistory
                    {
                        Id = Guid.CreateVersion7(),
                        ConversationId = projectConversation.Id,
                        TaskId = taskId,
                        Status = TaskExecutionStatus.Succeeded,
                        AgentName = message.AuthorName,
                        ConversationSequence = nextSequence,
                        ConversationPayload = JsonSerializer.Serialize(message, _jsonSerializerOptions),
                        Metadata = CreateMetadata(message, historyScope),
                        CreateTime = now,
                        UpdateTime = now,
                    }
                );
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsResult(ChatMessage message) =>
        message.AdditionalProperties?.TryGetValue("type", out var type) == true
        && string.Equals(type?.ToString(), "result", StringComparison.Ordinal);

    private static bool IsExcludedFromModelHistory(ChatMessage message) =>
        ConversationHistoryMetadata.IsModelHistoryExcluded(message)
        || ConversationHistoryMetadata.IsPersistenceExcluded(message)
        || ConversationHistoryMetadata.IsUserMemoryContext(message)
        || IsResult(message)
        || IsCheckpoint(message)
        || IsToolMessage(message);

    private static bool IsCheckpoint(ChatMessage message) =>
        message.AdditionalProperties?.TryGetValue("type", out var type) == true
        && string.Equals(type?.ToString(), "agentflow-checkpoint", StringComparison.Ordinal);

    private static bool IsToolMessage(ChatMessage message) => message.AdditionalProperties.IsToolMessage();

    private static ChatMessage? RemoveBlankTextualContent(ChatMessage message)
    {
        var contents = message.Contents.WithoutBlankTextualContent(message.AdditionalProperties);
        if (contents.Count == 0)
        {
            return null;
        }

        if (contents.Count == message.Contents.Count)
        {
            return message;
        }

        var filteredMessage = message.Clone();
        filteredMessage.Contents = contents;
        return filteredMessage;
    }

    private static bool HasHistoryScope(ProjectConversationChatHistory record, string? historyScope)
    {
        string? recordScope = null;
        if (
            record.Metadata?.TryGetValue(HistoryScopeMetadataKey, out var scopeElement) == true
            && scopeElement.ValueKind == JsonValueKind.String
        )
        {
            recordScope = scopeElement.GetString();
        }

        return string.Equals(recordScope, historyScope, StringComparison.Ordinal);
    }

    private static Dictionary<string, JsonElement>? CreateMetadata(ChatMessage message, string? historyScope)
    {
        var metadata = ProjectConversationChatHistoryMetadataFactory.FromMessage(message);
        if (historyScope == null)
        {
            return metadata;
        }

        metadata ??= [];
        metadata[HistoryScopeMetadataKey] = JsonSerializer.SerializeToElement(historyScope);
        return metadata;
    }

    private static List<ChatMessage> RemoveIncompleteFunctionCallsAndOrphanedResults(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatMessage> followingMessages
    )
    {
        // Per-service-call persistence stores the assistant function call before invoking the tool.
        // Its result arrives as the next service call's request, so pairing must cross that boundary.
        var allMessages = followingMessages.Count == 0 ? messages : messages.Concat(followingMessages).ToList();
        var result = new List<ChatMessage>(messages.Count);
        for (var index = 0; index < messages.Count; )
        {
            var message = allMessages[index];
            var functionCalls =
                message.Role == ChatRole.Assistant ? message.Contents.OfType<FunctionCallContent>().ToList() : [];
            if (functionCalls.Count > 0)
            {
                var toolMessageEnd = index + 1;
                while (
                    toolMessageEnd < allMessages.Count
                    && (
                        allMessages[toolMessageEnd].Role == ChatRole.Tool
                        || toolMessageEnd >= messages.Count
                            && allMessages[toolMessageEnd].GetAgentRequestMessageSourceType()
                                == AgentRequestMessageSourceType.AIContextProvider
                    )
                )
                {
                    toolMessageEnd++;
                }

                var resultCallIds = allMessages
                    .Skip(index + 1)
                    .Take(toolMessageEnd - index - 1)
                    .SelectMany(toolMessage => toolMessage.Contents)
                    .OfType<FunctionResultContent>()
                    .Select(content => content.CallId)
                    .ToHashSet(StringComparer.Ordinal);
                var matchedCallIds = functionCalls
                    .Select(content => content.CallId)
                    .Where(resultCallIds.Contains)
                    .ToHashSet(StringComparer.Ordinal);

                AddFilteredMessage(
                    result,
                    message,
                    message
                        .Contents.Where(content =>
                            content is not FunctionCallContent functionCall
                            || matchedCallIds.Contains(functionCall.CallId)
                        )
                        .ToList()
                );

                var pendingCallIds = new HashSet<string>(matchedCallIds, StringComparer.Ordinal);
                for (
                    var toolMessageIndex = index + 1;
                    toolMessageIndex < toolMessageEnd && toolMessageIndex < messages.Count;
                    toolMessageIndex++
                )
                {
                    var toolMessage = allMessages[toolMessageIndex];
                    AddFilteredMessage(
                        result,
                        toolMessage,
                        toolMessage
                            .Contents.Where(content =>
                                content is not FunctionResultContent functionResult
                                || pendingCallIds.Remove(functionResult.CallId)
                            )
                            .ToList()
                    );
                }

                index = toolMessageEnd;
                continue;
            }

            if (message.Role == ChatRole.Tool)
            {
                AddFilteredMessage(
                    result,
                    message,
                    message.Contents.Where(content => content is not FunctionResultContent).ToList()
                );
            }
            else
            {
                result.Add(message);
            }

            index++;
        }

        return result;
    }

    private static string ResolveCurrentUserId() => UserInfoUtil.RequiredUserId;

    private static void AddFilteredMessage(
        ICollection<ChatMessage> destination,
        ChatMessage source,
        IList<AIContent> contents
    )
    {
        if (contents.Count == 0)
        {
            return;
        }

        if (contents.Count == source.Contents.Count)
        {
            destination.Add(source);
            return;
        }

        var filteredMessage = source.Clone();
        filteredMessage.Contents = contents;
        destination.Add(filteredMessage);
    }

    private static ChatMessage? RemoveUnansweredToolApprovalRequests(
        ChatMessage message,
        IReadOnlySet<string> approvalResponseCallIds
    )
    {
        var contents = message
            .Contents.Where(content =>
                content
                    is not ToolApprovalRequestContent
                    {
                        ToolCall: FunctionCallContent { InformationalOnly: false } functionCall
                    }
                || approvalResponseCallIds.Contains(functionCall.CallId)
            )
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

    private static Task<Project?> ResolveProjectAsync(
        DbContext dbContext,
        string projectId,
        CancellationToken cancellationToken
    )
    {
        var normalizedProjectId = projectId.Trim();
        if (Guid.TryParse(normalizedProjectId, out var projectGuid))
        {
            return dbContext
                .Set<Project>()
                .SingleOrDefaultAsync(project => project.Id == projectGuid, cancellationToken);
        }

        return dbContext
            .Set<Project>()
            .SingleOrDefaultAsync(
                project => project.Name.ToLower() == normalizedProjectId.ToLower(),
                cancellationToken
            );
    }

    private static string? ExtractFirstText(ChatMessage? message)
    {
        if (message == null)
        {
            return null;
        }

        return string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text)).Trim();
    }

    private static IEnumerable<ChatMessage> AddResponseMetadata(
        IEnumerable<ChatMessage> messages,
        string? nodeName,
        string? agentName
    )
    {
        var normalizedNodeName = string.IsNullOrWhiteSpace(nodeName) ? null : nodeName.Trim();
        var normalizedAgentName = string.IsNullOrWhiteSpace(agentName) ? null : agentName.Trim();
        if (normalizedNodeName == null && normalizedAgentName == null)
        {
            return messages;
        }

        return messages.Select(message => AddResponseMetadata(message, normalizedNodeName, normalizedAgentName));
    }

    private static ChatMessage AddResponseMetadata(ChatMessage message, string? nodeName, string? agentName)
    {
        var shouldAddNodeName =
            nodeName != null && message.AdditionalProperties?.ContainsKey(NodeNamePropertyName) != true;
        var shouldAddAgentName =
            agentName != null
            && string.IsNullOrWhiteSpace(message.AuthorName)
            && message.AdditionalProperties?.ContainsKey(AgentNamePropertyName) != true;
        if (!shouldAddNodeName && !shouldAddAgentName)
        {
            return message;
        }

        var result = message.Clone();
        result.AdditionalProperties =
            message.AdditionalProperties == null
                ? new AdditionalPropertiesDictionary()
                : new AdditionalPropertiesDictionary(message.AdditionalProperties);
        if (shouldAddNodeName)
        {
            result.AdditionalProperties[NodeNamePropertyName] = nodeName;
        }

        if (shouldAddAgentName)
        {
            result.AdditionalProperties[AgentNamePropertyName] = agentName;
        }

        return result;
    }

    public sealed record State
    {
        public string ContextId { get; init; }

        public Guid ProjectId { get; init; }

        public string? HistoryScope { get; init; }

        public string? NodeName { get; init; }

        public State(string contextId, Guid projectId, string? historyScope = null, string? nodeName = null)
        {
            ContextId = contextId;
            ProjectId = projectId;
            HistoryScope = historyScope;
            NodeName = string.IsNullOrWhiteSpace(nodeName) ? null : nodeName.Trim();
        }
    }
}
