using System.Text.Json;
using Agw.Projects.Domain.Services;
using Agw.Shared;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Infrastructure;

public sealed class ConversationHandoffProvider : IConversationHandoffProvider
{
    private const int MaxCharacters = 32_000;
    private const string HistoryScopeMetadataKey = "historyScope";
    private const string TargetTypeMetadataKey = "targetType";
    private const string TargetIdMetadataKey = "targetId";

    private readonly IRepository<ProjectConversationChatHistory> _recordRepository;

    public ConversationHandoffProvider(IRepository<ProjectConversationChatHistory> recordRepository)
    {
        _recordRepository = recordRepository;
    }

    public async Task<ConversationHandoff> CreateAsync(
        Guid conversationId,
        AgentRuntimeType targetType,
        Guid targetId,
        CancellationToken cancellationToken = default
    )
    {
        if (conversationId == Guid.Empty || targetId == Guid.Empty)
        {
            return ConversationHandoff.Empty;
        }

        var records = await _recordRepository
            .Queryable.AsNoTracking()
            .Where(record => record.ConversationId == conversationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        records = records
            .OrderBy(record => record.ConversationSequence ?? long.MinValue)
            .ThenBy(record => record.CreateTime)
            .ThenBy(record => record.Id)
            .ToList();
        if (records.Count == 0)
        {
            return ConversationHandoff.Empty;
        }

        var target = new TargetIdentity(targetType, targetId);
        var attributedRecords = AttributeTargets(records);
        var cursor = attributedRecords
            .Where(item => item.Target == target)
            .Select(item => GetThroughSequence(item.Record.Metadata))
            .OfType<long>()
            .DefaultIfEmpty(-1)
            .Max();
        var throughSequence = records
            .Select(record => record.ConversationSequence)
            .OfType<long>()
            .DefaultIfEmpty(-1)
            .Max();

        var candidates = attributedRecords
            .Where(item => item.Sequence > cursor)
            .Where(item => item.Target != target)
            .Where(item => !IsAlreadyVisibleToTarget(item.Record, targetType))
            .Select(CreateCandidate)
            .OfType<HandoffCandidate>()
            .ToList();
        candidates = DeduplicateByMessageId(candidates);

        var selected = SelectRecentMessages(candidates);
        return new ConversationHandoff(selected, throughSequence >= 0 ? throughSequence : null);
    }

    private static List<AttributedRecord> AttributeTargets(IReadOnlyList<ProjectConversationChatHistory> records)
    {
        var result = new List<AttributedRecord>(records.Count);
        TargetIdentity? activeTarget = null;
        foreach (var record in records)
        {
            var explicitTarget = GetTarget(record);
            if (explicitTarget.HasValue)
            {
                activeTarget = explicitTarget;
            }

            result.Add(new AttributedRecord(record, record.ConversationSequence ?? long.MinValue, activeTarget));
        }

        return result;
    }

    private static TargetIdentity? GetTarget(ProjectConversationChatHistory record)
    {
        if (TryGetTargetFromMetadata(record.Metadata, out var target))
        {
            return target;
        }

        var historyScope = GetHistoryScope(record);
        if (historyScope == null || !historyScope.StartsWith("agentflow:", StringComparison.Ordinal))
        {
            return null;
        }

        var separatorIndex = historyScope.IndexOf(":node:", StringComparison.Ordinal);
        var idText =
            separatorIndex < 0
                ? historyScope["agentflow:".Length..]
                : historyScope["agentflow:".Length..separatorIndex];
        return Guid.TryParse(idText, out var agentflowId)
            ? new TargetIdentity(AgentRuntimeType.Agentflow, agentflowId)
            : null;
    }

    private static bool TryGetTargetFromMetadata(
        IReadOnlyDictionary<string, JsonElement>? metadata,
        out TargetIdentity target
    )
    {
        target = default;
        if (
            metadata?.TryGetValue(TargetTypeMetadataKey, out var typeElement) != true
            || metadata.TryGetValue(TargetIdMetadataKey, out var idElement) != true
            || typeElement.ValueKind != JsonValueKind.String
            || idElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idElement.GetString(), out var targetId)
        )
        {
            return false;
        }

        var targetType = typeElement.GetString() switch
        {
            "agent" => AgentRuntimeType.Agent,
            "agentflow" => AgentRuntimeType.Agentflow,
            _ => (AgentRuntimeType?)null,
        };
        if (!targetType.HasValue)
        {
            return false;
        }

        target = new TargetIdentity(targetType.Value, targetId);
        return true;
    }

    private static long? GetThroughSequence(IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (metadata?.TryGetValue(ConversationHandoffMetadata.ThroughSequenceKey, out var sequenceElement) != true)
        {
            return null;
        }

        if (sequenceElement.ValueKind == JsonValueKind.Number && sequenceElement.TryGetInt64(out var sequence))
        {
            return sequence;
        }

        return
            sequenceElement.ValueKind == JsonValueKind.String
            && long.TryParse(sequenceElement.GetString(), out sequence)
            ? sequence
            : null;
    }

    private static bool IsAlreadyVisibleToTarget(ProjectConversationChatHistory record, AgentRuntimeType targetType)
    {
        if (targetType != AgentRuntimeType.Agent || GetHistoryScope(record) != null)
        {
            return false;
        }

        var message = record.ToChatMessage();
        return message == null || !HasMessageType(message, "result");
    }

    private static string? GetHistoryScope(ProjectConversationChatHistory record)
    {
        if (
            record.Metadata?.TryGetValue(HistoryScopeMetadataKey, out var scopeElement) != true
            || scopeElement.ValueKind != JsonValueKind.String
        )
        {
            return null;
        }

        return scopeElement.GetString();
    }

    private static HandoffCandidate? CreateCandidate(AttributedRecord item)
    {
        var message = item.Record.ToChatMessage();
        if (
            message == null
            || ConversationHandoffMetadata.IsHandoffMessage(message)
            || ConversationHistoryMetadata.IsModelHistoryExcluded(message)
        )
        {
            return null;
        }

        var isResult = HasMessageType(message, "result");
        if (message.Role == ChatRole.User)
        {
            if (!IsRealUserMessage(message, item.Record.Metadata))
            {
                return null;
            }
        }
        else if (message.Role != ChatRole.Assistant && !isResult)
        {
            return null;
        }

        if (IsControlMessage(message))
        {
            return null;
        }

        var textContents = message
            .Contents.OfType<TextContent>()
            .Where(content => !string.IsNullOrWhiteSpace(content.Text))
            .Select(content => (AIContent)new TextContent(content.Text))
            .ToList();
        if (textContents.Count == 0)
        {
            return null;
        }

        var handoffMessage = new ChatMessage(isResult ? ChatRole.Assistant : message.Role, textContents)
        {
            MessageId = message.MessageId,
            AuthorName = message.AuthorName,
        }.WithAgentRequestMessageSource(
            AgentRequestMessageSourceType.AIContextProvider,
            nameof(ConversationHandoffProvider)
        );
        ConversationHandoffMetadata.MarkHandoffMessage(handoffMessage);

        return new HandoffCandidate(
            item.Sequence,
            handoffMessage,
            textContents.OfType<TextContent>().Sum(content => content.Text.Length)
        );
    }

    private static bool IsRealUserMessage(ChatMessage message, IReadOnlyDictionary<string, JsonElement>? metadata) =>
        string.Equals(message.AuthorName, Constants.DefaultInputAuthor, StringComparison.Ordinal)
        || metadata?.ContainsKey(TargetTypeMetadataKey) == true;

    private static bool IsControlMessage(ChatMessage message)
    {
        if (message.AdditionalProperties.IsToolMessage())
        {
            return true;
        }

        return GetMessageType(message)
            is "agentflow-checkpoint"
                or "human-interaction-request"
                or "human-gate-request"
                or "tool-approval-request"
                or "turn-start"
                or "turn-finished";
    }

    private static bool HasMessageType(ChatMessage message, string expected) =>
        string.Equals(GetMessageType(message), expected, StringComparison.Ordinal);

    private static string? GetMessageType(ChatMessage message) =>
        message.AdditionalProperties?.TryGetValue("type", out var value) == true ? value?.ToString() : null;

    private static List<HandoffCandidate> DeduplicateByMessageId(IReadOnlyList<HandoffCandidate> candidates)
    {
        var lastSequenceByMessage = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Message.MessageId))
            .GroupBy(candidate => candidate.Message.MessageId!, candidate => candidate.Sequence)
            .ToDictionary(group => group.Key, group => group.Max(), StringComparer.Ordinal);

        return candidates
            .Where(candidate =>
                string.IsNullOrWhiteSpace(candidate.Message.MessageId)
                || lastSequenceByMessage[candidate.Message.MessageId!] == candidate.Sequence
            )
            .ToList();
    }

    private static IReadOnlyList<ChatMessage> SelectRecentMessages(IReadOnlyList<HandoffCandidate> candidates)
    {
        var selected = new List<HandoffCandidate>();
        var characterCount = 0;
        for (var index = candidates.Count - 1; index >= 0; index--)
        {
            var candidate = candidates[index];
            if (selected.Count > 0 && characterCount + candidate.CharacterCount > MaxCharacters)
            {
                break;
            }

            selected.Add(candidate);
            characterCount += candidate.CharacterCount;
        }

        selected.Reverse();
        return selected.Select(candidate => candidate.Message).ToList();
    }

    private readonly record struct TargetIdentity(AgentRuntimeType Type, Guid Id);

    private sealed record AttributedRecord(
        ProjectConversationChatHistory Record,
        long Sequence,
        TargetIdentity? Target
    );

    private sealed record HandoffCandidate(long Sequence, ChatMessage Message, int CharacterCount);
}
