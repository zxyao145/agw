using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Agw.Auth.Contracts;
using Agw.Projects.Application.History;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.History;
using Agw.Projects.Domain.Rules;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Infrastructure;

/// <summary>
/// Turn 表与用户输入行的写入：受理、状态推进与结束都使用当前作用域的持久化上下文，调用方的事务与租约检查因此覆盖这些写入。
/// Writes the turn table and the user input row: acceptance, status progress and completion use the current scope's persistence context, so the caller's transaction and lease check cover them.
/// </summary>
public sealed class ConversationTurnStore : IConversationTurnStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly IProjectsDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public ConversationTurnStore(IProjectsDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<ConversationTurnAcceptance> AcceptAsync(
        AcceptConversationTurnRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TurnId == Guid.Empty || request.ConversationId == Guid.Empty || request.TargetId == Guid.Empty)
            throw new AgwException(ErrorCodes.InvalidParam, "turnId, conversationId and targetId are required.");
        var owner = UserInfoUtil.RequiredUserId;
        var existing = await _dbContext
            .ProjectConversationTurns.AsNoTracking()
            .SingleOrDefaultAsync(turn => turn.Id == request.TurnId, cancellationToken)
            .ConfigureAwait(false);
        if (existing != null)
        {
            // 同一用户在同一对话重发同一 turnId 才是同一次受理。
            // Only the same user resending the same turnId in the same conversation is the same acceptance.
            return existing.ProjectConversationId == request.ConversationId
                ? new ConversationTurnAcceptance(Map(existing), Created: false)
                : throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        var conversation =
            await _dbContext
                .ProjectConversations.SingleOrDefaultAsync(
                    item =>
                        item.Id == request.ConversationId
                        && item.ProjectId == request.ProjectId
                        && item.CreateBy == owner
                        && item.Project!.CreateBy == owner,
                    cancellationToken
                )
                .ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound);
        if (conversation.Generation != request.Generation)
            throw new AgwException(ErrorCodes.ConversationSessionConflict);

        var sequence =
            (
                await _dbContext
                    .ProjectConversationChatHistories.Where(history => history.ConversationId == conversation.Id)
                    .MaxAsync(history => history.ConversationSequence, cancellationToken)
                    .ConfigureAwait(false)
                ?? -1
            ) + 1;
        var now = _timeProvider.GetUtcNow();
        if (request.Input is { } input)
            AddInput(request, conversation, input, sequence, now);

        var turn = new ProjectConversationTurn
        {
            Id = request.TurnId,
            ProjectConversationId = conversation.Id,
            TaskId = request.TaskId,
            TargetId = request.TargetId,
            RuntimeType =
                request.TargetType == ConversationTurnTargetType.Agentflow
                    ? AgentRuntimeType.Agentflow
                    : AgentRuntimeType.Agent,
            Status = ProjectConversationTurnStatus.Accepted,
            InputMessageId = request.Input?.MessageId ?? Guid.Empty,
            FirstSequence = sequence,
            StartedAt = now,
        };
        _dbContext.ProjectConversationTurns.Add(turn);
        await _dbContext
            .SaveConversationChangesAsync(conversation.Id, request.Generation, cancellationToken)
            .ConfigureAwait(false);
        return new ConversationTurnAcceptance(Map(turn), Created: true);
    }

    public async Task<ConversationTurnSnapshot?> GetAsync(Guid turnId, CancellationToken cancellationToken)
    {
        var turn = await _dbContext
            .ProjectConversationTurns.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == turnId, cancellationToken)
            .ConfigureAwait(false);
        return turn == null ? null : Map(turn);
    }

    public async Task MarkRunningAsync(Guid turnId, CancellationToken cancellationToken)
    {
        var turn = await LoadAsync(turnId, cancellationToken).ConfigureAwait(false);
        if (turn.Status != ProjectConversationTurnStatus.Accepted)
            return;
        turn.Status = ProjectConversationTurnStatus.Running;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteStepAsync(Guid turnId, int stepCount, CancellationToken cancellationToken)
    {
        var turn = await LoadAsync(turnId, cancellationToken).ConfigureAwait(false);
        if (IsFinished(turn.Status) || stepCount <= turn.StepCount)
            return;
        turn.StepCount = stepCount;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FinishAsync(
        Guid turnId,
        ConversationTurnStatus status,
        int stepCount,
        string? errorCode,
        CancellationToken cancellationToken
    )
    {
        if (
            status
            is not (
                ConversationTurnStatus.Completed
                or ConversationTurnStatus.Failed
                or ConversationTurnStatus.Interrupted
            )
        )
            throw new AgwException(ErrorCodes.InvalidParam, $"Turn status '{status}' is not terminal.");
        var turn = await LoadAsync(turnId, cancellationToken).ConfigureAwait(false);
        if (IsFinished(turn.Status))
            return;
        turn.Status = (ProjectConversationTurnStatus)status;
        turn.StepCount = Math.Max(turn.StepCount, stepCount);
        turn.ErrorCode = errorCode;
        turn.FinishedAt = _timeProvider.GetUtcNow();
        turn.LastSequence = await _dbContext
            .ProjectConversationChatHistories.Where(history => history.TurnId == turnId)
            .MaxAsync(history => history.ConversationSequence, cancellationToken)
            .ConfigureAwait(false);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private void AddInput(
        AcceptConversationTurnRequest request,
        ProjectConversation conversation,
        ConversationTurnInput input,
        long sequence,
        DateTimeOffset now
    )
    {
        var message =
            JsonSerializer.Deserialize<ChatMessage>(input.Payload, JsonOptions)
            ?? throw new AgwException(ErrorCodes.InvalidParam, "The turn input is not a message.");
        if (!Guid.TryParse(message.MessageId, out var messageId) || messageId != input.MessageId)
            throw new AgwException(ErrorCodes.InvalidParam, "The turn input message ID does not match its row.");
        var metadata = ProjectConversationChatHistoryMetadataFactory.FromMessage(message) ?? [];
        // 输入行记录受理时的对话位置，交接从这里之后继续。
        // The input row records the conversation position at acceptance; handoffs continue after it.
        metadata[ConversationHandoffMetadata.ThroughSequenceKey] = JsonSerializer.SerializeToElement(sequence);
        metadata["producerScopeId"] = JsonSerializer.SerializeToElement(request.TurnId);
        metadata["generation"] = JsonSerializer.SerializeToElement(request.Generation);
        _dbContext.ProjectConversationChatHistories.Add(
            new ProjectConversationChatHistory
            {
                Id = input.MessageId,
                ConversationId = conversation.Id,
                TaskId = request.TurnId,
                Status = TaskExecutionStatus.Succeeded,
                AgentName = input.Author,
                ConversationSequence = sequence,
                ConversationPayload = input.Payload,
                Metadata = metadata,
                TurnId = request.TurnId,
                StepIndex = 0,
                Purpose = ConversationMessagePurpose.Input,
                CreateTime = input.CreatedAt,
                UpdateTime = now,
            }
        );
        var title = TaskTitleRules.Create(
            string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text)).Trim()
        );
        if (
            string.Equals(conversation.Title, TaskTitleRules.DefaultTitle, StringComparison.Ordinal)
            && !string.Equals(title, TaskTitleRules.DefaultTitle, StringComparison.Ordinal)
        )
            conversation.Title = title;
    }

    private async Task<ProjectConversationTurn> LoadAsync(Guid turnId, CancellationToken cancellationToken) =>
        await _dbContext
            .ProjectConversationTurns.SingleOrDefaultAsync(turn => turn.Id == turnId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new AgwException(ErrorCodes.ResourceNotFound);

    private static bool IsFinished(ProjectConversationTurnStatus status) =>
        status
            is ProjectConversationTurnStatus.Completed
                or ProjectConversationTurnStatus.Failed
                or ProjectConversationTurnStatus.Interrupted;

    internal static ConversationTurnSnapshot Map(ProjectConversationTurn turn) =>
        new(
            turn.Id,
            turn.ProjectConversationId,
            turn.TaskId,
            turn.TargetId,
            turn.RuntimeType == AgentRuntimeType.Agentflow
                ? ConversationTurnTargetType.Agentflow
                : ConversationTurnTargetType.Agent,
            (ConversationTurnStatus)turn.Status,
            turn.InputMessageId,
            turn.FirstSequence,
            turn.LastSequence,
            turn.StepCount,
            turn.StartedAt,
            turn.FinishedAt,
            turn.ErrorCode
        );
}
