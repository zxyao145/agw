using Agw.Shared;
using Agw.Shared.Enums;
using Agw.Shared.Models;
using Agw.Shared.Tasks;
using Agw.Shared.Tasks.Entities;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Agw.Domain.Services;

/// <summary>
/// Persists agent chat history in EF Core while keeping the conversation key in session state.
/// </summary>
public sealed class EfCoreChatHistoryProvider : ChatHistoryProvider, IProviderSessionState
{
    private static readonly JsonSerializerOptions DefaultJsonSerializerOptions = new(JsonSerializerDefaults.Web);
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
                var contextId = Activity.Current?.TraceId.ToString();
                contextId ??= Guid.NewGuid().Normalize();
                var sessionId = Guid.NewGuid().Normalize();
                return new State(contextId, sessionId, null);
            },
            nameof(EfCoreChatHistoryProvider),
            _jsonSerializerOptions);
    }

    public void InitializeSessionState(
        AgentSession session,
        string contextId,
        string? sessionId,
        string? projectId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);
        if(Guid.TryParse(contextId, out var guidContextId))
        {
            contextId = guidContextId.Normalize();
        }

        var state = new State(
            contextId.Trim(),
            string.IsNullOrWhiteSpace(sessionId) ? contextId.Trim() : sessionId.Trim(),
            string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim());
        _state.SaveState(session, state);
    }

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var state = _state.GetOrInitializeState(context.Session);
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();

        var payloads = await dbContext.Set<TaskRecord>()
            .AsNoTracking()
            .Where(record => record.ContextId == state.ContextId
                && record.ConversationPayload != null)
            .OrderBy(record => record.ConversationSequence)
            .ThenBy(record => record.CreateTime)
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
        var projectTask = await dbContext.Set<ProjectTask>()
            .SingleOrDefaultAsync(x => x.ContextId == state.ContextId, cancellationToken)
            .ConfigureAwait(false);

        if (projectTask == null)
        {
            var firstUserText = ExtractFirstText(newMessages.FirstOrDefault(message => message.Role == ChatRole.User));
            projectTask = new ProjectTask
            {
                Id = Guid.NewGuid(),
                ProjectId = state.ProjectId ?? string.Empty,
                ContextId = state.ContextId,
                Title = string.IsNullOrWhiteSpace(firstUserText) ? "New Chat" : firstUserText[..Math.Min(firstUserText.Length, 80)],
                Description = firstUserText ?? string.Empty,
                Status = ProjectTaskStatus.Succeeded,
                FinishedTime = now,
                CreateBy = DefaultUser,
                CreateTime = now,
                UpdateBy = DefaultUser,
                UpdateTime = now
            };
            dbContext.Set<ProjectTask>().Add(projectTask);
        }
        else
        {
            projectTask.UpdateBy = DefaultUser;
            projectTask.UpdateTime = now;
        }

        var nextSequence = await dbContext.Set<TaskRecord>()
            .Where(x => x.ContextId == state.ContextId)
            .Select(x => x.ConversationSequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? -1;

        foreach (var message in newMessages)
        {
            //if (string.IsNullOrWhiteSpace(message.AuthorName))
            //{
            //    continue;
            //}
            nextSequence++;

            dbContext.Set<TaskRecord>().Add(new TaskRecord
            {
                Id = Guid.NewGuid(),
                ContextId = state.ContextId,
                SessionId = state.SessionId ?? state.ContextId,
                AgentName = message.AuthorName,
                ConversationSequence = nextSequence,
                ConversationPayload = JsonSerializer.Serialize(message, _jsonSerializerOptions),
                CreateTime = now,
                UpdateTime = now
            });
        }

        _state.SaveState(context.Session, state);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

    public sealed record State(string ContextId, string? SessionId, string? ProjectId);
}
