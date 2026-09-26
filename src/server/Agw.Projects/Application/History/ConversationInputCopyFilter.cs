using System.Text.Json;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Application.History;

internal static class ConversationInputCopyFilter
{
    public static async Task<List<ProjectConversationChatHistory>> FilterAsync(
        IProjectsDbContext dbContext,
        Guid conversationId,
        IReadOnlyList<ProjectConversationChatHistory> records,
        CancellationToken cancellationToken
    )
    {
        var candidateTurnIds = records.Where(IsCandidate).Select(record => record.TurnId!.Value).Distinct().ToArray();
        if (candidateTurnIds.Length == 0)
            return records.ToList();

        var turns = await dbContext
            .ProjectConversationTurns.AsNoTracking()
            .Where(turn => turn.ProjectConversationId == conversationId && candidateTurnIds.Contains(turn.Id))
            .ToListAsync(cancellationToken);
        var inputIds = turns.Select(turn => turn.InputMessageId).Where(id => id != Guid.Empty).Distinct().ToArray();
        if (inputIds.Length == 0)
            return records.ToList();

        var inputs = await dbContext
            .ProjectConversationChatHistories.AsNoTracking()
            .Where(record => record.ConversationId == conversationId && inputIds.Contains(record.Id))
            .ToDictionaryAsync(record => record.Id, cancellationToken);
        var inputByTurn = turns
            .Where(turn => inputs.ContainsKey(turn.InputMessageId))
            .ToDictionary(turn => turn.Id, turn => inputs[turn.InputMessageId]);

        return records
            .Where(record =>
                !IsCandidate(record)
                || !inputByTurn.TryGetValue(record.TurnId!.Value, out var input)
                || !IsCopy(record, input)
            )
            .ToList();
    }

    private static bool IsCandidate(ProjectConversationChatHistory record) =>
        record.Purpose == ConversationMessagePurpose.Message
        && record.StepIndex == 0
        && record.TurnId.HasValue
        && record.HistoryScope?.StartsWith("agentflow:", StringComparison.Ordinal) == true
        && record.HistoryScope.Contains(":node:", StringComparison.Ordinal)
        && record.ConversationPayload != null;

    private static bool IsCopy(ProjectConversationChatHistory record, ProjectConversationChatHistory input)
    {
        if (input.Purpose != ConversationMessagePurpose.Input || input.TurnId != record.TurnId)
            return false;

        var message = record.ToChatMessage();
        var original = input.ToChatMessage();
        if (
            message?.Role != ChatRole.User
            || original?.Role != ChatRole.User
            || !string.Equals(message.AuthorName, original.AuthorName, StringComparison.Ordinal)
            || message.AdditionalProperties?.TryGetValue("agentflowInput", out var nodeInput) == true
                && string.Equals(nodeInput?.ToString(), bool.TrueString, StringComparison.OrdinalIgnoreCase)
        )
            return false;

        using var messagePayload = JsonDocument.Parse(record.ConversationPayload!);
        using var inputPayload = JsonDocument.Parse(input.ConversationPayload!);
        return JsonElement.DeepEquals(
            messagePayload.RootElement.GetProperty("contents"),
            inputPayload.RootElement.GetProperty("contents")
        );
    }
}
