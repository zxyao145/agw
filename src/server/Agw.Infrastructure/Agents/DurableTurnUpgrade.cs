using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Contracts.Execution;
using Agw.Agents.Contracts.Messages;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Shared;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Infrastructure.Agents;

/// <summary>
/// Host 启动时补齐活动执行的 Turn；每条记录在验证所属用户后原子转换。
/// Completes active executions' turns at Host startup, converting each record atomically after owner validation.
/// </summary>
public sealed class DurableTurnUpgrade
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IApplicationLock _locks;
    private readonly TimeProvider _clock;

    public DurableTurnUpgrade(IServiceScopeFactory scopes, IApplicationLock locks, TimeProvider clock)
    {
        _scopes = scopes;
        _locks = locks;
        _clock = clock;
    }

    public async Task UpgradeAsync(CancellationToken cancellationToken)
    {
        await using var upgradeLock = await _locks.AcquireAsync("durable-turn-upgrade", cancellationToken);
        await using var scan = _scopes.CreateAsyncScope();
        var database = scan.ServiceProvider.GetRequiredService<AgwDbContext>();
        var candidates = await DbSeeder.ReadDurableTurnUpgradeCandidatesAsync(database, cancellationToken);
        foreach (var (id, userId) in candidates)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw Invalid(id);
            using var owner = UserInfoUtil.Push(
                new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "DurableTurnUpgrade")
                )
            );
            await UpgradeExecutionAsync(id, userId, cancellationToken);
        }
    }

    private async Task UpgradeExecutionAsync(Guid id, string userId, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var execution = await db.DurableExecutions.SingleAsync(record => record.Id == id, cancellationToken);
        var identity = DurableExecutionManifestScopeReader.Read(execution.ManifestJson, id, userId);
        if (
            identity == null
            || !execution.ScopeBackfilled
            || execution.ProjectId != identity.ProjectId
            || execution.ProjectConversationId != identity.ProjectConversationId
        )
            throw Invalid(id);
        var manifest = JsonUtil.Deserialize<DurableExecutionManifest>(execution.ManifestJson)!;
        await using var lifecycle = await _locks.AcquireAsync(
            ProjectLifecycleLock.GetResourceName(identity.ProjectId),
            cancellationToken
        );
        await using var historyLock = await _locks.AcquireAsync(
            ConversationHistoryLock.GetResourceName(identity.ProjectId, manifest.Task.ContextId),
            cancellationToken
        );
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var conversation =
            await db.ProjectConversations.SingleOrDefaultAsync(
                item =>
                    item.Id == identity.ProjectConversationId
                    && item.ProjectId == identity.ProjectId
                    && item.CreateBy == userId
                    && item.Project!.CreateBy == userId,
                cancellationToken
            ) ?? throw Invalid(id);
        if (conversation.Generation != manifest.Task.Generation)
            throw Invalid(id);

        var history = await db
            .ProjectConversationChatHistories.Where(row =>
                row.ConversationId == conversation.Id && row.ConversationSequence != null
            )
            .OrderBy(row => row.ConversationSequence)
            .ToListAsync(cancellationToken);
        var inputs = history
            .Where(row => row.Purpose == ConversationMessagePurpose.Input && row.HistoryScope == null)
            .Where(row =>
                row.Id.ToString("D") == manifest.Input.MessageId
                || row.ConversationPayload != null
                    && JsonSerializer
                        .Deserialize<ChatMessage>(row.ConversationPayload, WebJsonOptions.Default)
                        ?.MessageId == manifest.Input.MessageId
            )
            .ToArray();
        if (inputs.Length > 1)
            throw Invalid(id);
        var input = inputs.SingleOrDefault();
        var firstSequence = input?.ConversationSequence ?? ((history.LastOrDefault()?.ConversationSequence ?? -1) + 1);
        if (input == null && manifest.Input.Contents.Count > 0)
        {
            var messageId = Guid.TryParse(manifest.Input.MessageId, out var parsed) ? parsed : Guid.CreateVersion7();
            var message = CreateInput(manifest.Input, messageId, execution.CreateTime);
            var metadata = new Dictionary<string, JsonElement>();
            metadata["targetType"] = JsonSerializer.SerializeToElement(
                manifest.AgentType == AgentRuntimeType.Agentflow ? "agentflow" : "agent"
            );
            metadata["targetId"] = JsonSerializer.SerializeToElement(manifest.AgentId);
            metadata[ConversationHandoffMetadata.ThroughSequenceKey] = JsonSerializer.SerializeToElement(firstSequence);
            metadata["producerScopeId"] = JsonSerializer.SerializeToElement(id);
            metadata["generation"] = JsonSerializer.SerializeToElement(manifest.Task.Generation);
            input = new ProjectConversationChatHistory
            {
                Id = messageId,
                ConversationId = conversation.Id,
                TaskId = id,
                Status = TaskExecutionStatus.Succeeded,
                ConversationSequence = firstSequence,
                ConversationPayload = JsonSerializer.Serialize(message, WebJsonOptions.Default),
                Metadata = metadata,
                AgentName = manifest.Input.Author,
                TurnId = id,
                StepIndex = 0,
                Purpose = ConversationMessagePurpose.Input,
                CreateTime = message.CreatedAt!.Value,
                UpdateTime = message.CreatedAt.Value,
            };
            db.ProjectConversationChatHistories.Add(input);
        }
        if (input != null)
        {
            var previousTurnId = input.TurnId;
            if (previousTurnId.HasValue && previousTurnId != id)
            {
                if (await db.DurableExecutions.AnyAsync(row => row.Id == previousTurnId, cancellationToken))
                    throw Invalid(id);
                foreach (var row in history.Where(row => row.TurnId == previousTurnId))
                    row.TurnId = id;
                var previous = await db.ProjectConversationTurns.SingleOrDefaultAsync(
                    row => row.Id == previousTurnId,
                    cancellationToken
                );
                if (previous != null)
                    db.ProjectConversationTurns.Remove(previous);
            }
            input.TurnId = id;
            var message =
                JsonSerializer.Deserialize<ChatMessage>(input.ConversationPayload!, WebJsonOptions.Default)
                ?? throw Invalid(id);
            message.MessageId = input.Id.ToString("D");
            input.ConversationPayload = JsonSerializer.Serialize(message, WebJsonOptions.Default);
            manifest = manifest with
            {
                StreamingScopeId = manifest.StreamingScopeId ?? manifest.Input.MessageId,
                Input = new AgwUserInput
                {
                    MessageId = input.Id.ToString("D"),
                    CreatedAt = manifest.Input.CreatedAt ?? input.CreateTime,
                    Author = manifest.Input.Author,
                    Contents = manifest.Input.Contents,
                },
            };
            execution.ManifestJson = JsonUtil.Serialize(manifest);
        }

        var turn = await db.ProjectConversationTurns.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (turn == null)
        {
            turn = new ProjectConversationTurn { Id = id };
            db.ProjectConversationTurns.Add(turn);
        }
        turn.ProjectConversationId = conversation.Id;
        turn.TaskId = manifest.Task.TaskId;
        turn.TargetId = manifest.AgentId;
        turn.RuntimeType = manifest.AgentType;
        turn.InputMessageId = input?.Id ?? Guid.Empty;
        turn.FirstSequence = firstSequence;
        turn.Status =
            execution.Status == DurableExecutionStatus.Queued
                ? ProjectConversationTurnStatus.Accepted
                : ProjectConversationTurnStatus.Running;
        turn.StartedAt = execution.CreateTime;
        turn.FinishedAt = null;
        turn.LastSequence = null;
        turn.ErrorCode = null;
        if (
            execution.Status == DurableExecutionStatus.Running
            && execution.LeaseEpoch == 0
            && execution.LeaseExpiresAt == null
        )
        {
            execution.Status = DurableExecutionStatus.Resuming;
            execution.WorkerId = null;
        }
        execution.StateVersion = Guid.CreateVersion7();
        execution.StateChangedAt = _clock.GetUtcNow();
        await UpgradeEventsAsync(db, execution, manifest, cancellationToken);
        await db.SaveConversationChangesAsync(conversation.Id, manifest.Task.Generation, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task UpgradeEventsAsync(
        AgwDbContext db,
        DurableExecutionRecord execution,
        DurableExecutionManifest manifest,
        CancellationToken cancellationToken
    )
    {
        var events = await db
            .DurableExecutionEvents.Where(entry => entry.TurnId == execution.Id)
            .OrderBy(entry => entry.TurnSequence)
            .ToListAsync(cancellationToken);
        execution.LastEventSequence = events.LastOrDefault()?.TurnSequence ?? 0;
        var interactionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in events)
        {
            var message = JsonUtil.Deserialize<AgwMessage>(entry.PayloadJson) ?? throw Invalid(execution.Id);
            if (message.AdditionalProperties?.TryGetValue("interaction", out var interaction) == true)
            {
                var request = JsonUtil.Deserialize<InteractionRequest>(JsonUtil.Serialize(interaction));
                if (request != null)
                    interactionIds.Add(request.InteractionId);
            }
            if (
                message.AdditionalProperties?.TryGetValue("type", out var type) == true
                && type?.ToString() == "turn-start"
            )
            {
                message.AdditionalProperties["type"] = AgwMessageTypes.TurnStart;
                entry.PayloadJson = JsonUtil.Serialize(message);
            }
        }
        if (events.Count == 0)
        {
            Append(
                new AgwMessage(
                    Guid.CreateVersion7().ToString("D"),
                    Constants.DefaultAgentAuthor,
                    AiRole.System,
                    [new AgwTextContent { Content = "" }],
                    new AdditionalPropertiesDictionary
                    {
                        ["type"] = AgwMessageTypes.TurnStart,
                        ["turnId"] = execution.Id.ToString("D"),
                        ["conversationId"] = manifest.Task.ProjectConversationId.ToString("D"),
                        ["agentId"] = manifest.AgentId.ToString("D"),
                        ["agentType"] = manifest.AgentType == AgentRuntimeType.Agentflow ? "agentflow" : "agent",
                        ["streamingScopeId"] = manifest.StreamingScopeId ?? manifest.Input.MessageId,
                    }
                )
            );
        }
        if (execution.Status == DurableExecutionStatus.WaitingForHuman && execution.PendingInteractionsJson != null)
        {
            using var state = JsonDocument.Parse(execution.PendingInteractionsJson);
            var pending =
                state.RootElement.GetProperty("pending").Deserialize<InteractionRequest[]>(WebJsonOptions.Default)
                ?? throw Invalid(execution.Id);
            var responses =
                execution.ResponsesJson == null
                    ? []
                    : JsonUtil.Deserialize<InteractionResponse[]>(execution.ResponsesJson)
                        ?? throw Invalid(execution.Id);
            foreach (
                var request in pending.Where(request =>
                    !interactionIds.Contains(request.InteractionId)
                    && !responses.Any(response => response.InteractionId == request.InteractionId)
                )
            )
                Append(
                    InteractionMessageMapper.Create(
                        request,
                        Guid.CreateVersion7().ToString("D"),
                        execution.Id,
                        manifest.StreamingScopeId ?? manifest.Input.MessageId
                    )
                );
        }

        void Append(AgwMessage message) =>
            db.DurableExecutionEvents.Add(
                new DurableExecutionEventRecord
                {
                    Id = Guid.CreateVersion7(),
                    TurnId = execution.Id,
                    TurnSequence = ++execution.LastEventSequence,
                    LeaseEpoch = 0,
                    SegmentIndex = execution.SegmentIndex,
                    PayloadJson = JsonUtil.Serialize(message),
                    CreateBy = execution.UserId,
                    UpdateBy = execution.UserId,
                }
            );
    }

    private static ChatMessage CreateInput(AgwUserInput input, Guid id, DateTimeOffset createdAt)
    {
        var contents = input
            .Contents.Select(content =>
            {
                AIContent converted = content switch
                {
                    AgwTextContent text => new TextContent(text.Content),
                    AgwUriContent uri => new UriContent(uri.Uri, uri.MediaType),
                    AgwDataContent data => new DataContent(data.Data, data.MediaType) { Name = data.Name },
                    _ => throw new AgwException(ErrorCodes.InvalidParam, "The persisted input content is unsupported."),
                };
                converted.AdditionalProperties = content.AdditionalProperties;
                return converted;
            })
            .ToList();
        return new ChatMessage(ChatRole.User, contents)
        {
            MessageId = id.ToString("D"),
            AuthorName = input.Author,
            CreatedAt = input.CreatedAt ?? createdAt,
        };
    }

    private static AgwException Invalid(Guid id) =>
        new(
            ErrorCodes.DurableExecutionConflict,
            $"Execution '{id}' cannot be upgraded because its owner, manifest or conversation history is inconsistent."
        );
}
