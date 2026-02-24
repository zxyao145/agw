using DSystem.SessionRecords.Entities;
using DSystem.SessionRecords.Repositories;
using DSystem.Shared;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace DSystem.SessionRecords.Application;

public class SessionRecordApplication
{
    private readonly IAgentSessionRecordRepository _repository;
    private readonly ISessionRecordsUnitOfWork _unitOfWork;

    public SessionRecordApplication(
        IAgentSessionRecordRepository repository,
        ISessionRecordsUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task SaveThreadStateAsync(
        string sessionId,
        string projectId,
        JsonElement serializedThread,
        IReadOnlyCollection<AgentResponseUpdate> updates,
        string? input,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        var records = await _repository.ListAsync(session =>
            session.SessionId == sessionId && session.ProjectId == projectId);

        var byMessageId = records
            .Where(r => !string.IsNullOrWhiteSpace(r.MessageId))
            .ToDictionary(r => r.MessageId, StringComparer.Ordinal);

        var updatesToSave = new List<AgentResponseUpdate>(updates);
        AppendUserInput(updatesToSave, input);

        foreach (var update in updatesToSave)
        {
            var message = update.ToAiMessage();
            if (message == null)
            {
                continue;
            }

            var messageId = string.IsNullOrWhiteSpace(message.MessageId)
                ? $"msg_{Guid.NewGuid():N}"
                : message.MessageId;

            if (!byMessageId.TryGetValue(messageId, out var record))
            {
                record = new AgentSessionRecord
                {
                    ProjectId = projectId,
                    SessionId = sessionId,
                    MessageId = messageId,
                    CreateTime = DateTime.UtcNow
                };

                await _repository.AddAsync(record);
                byMessageId[messageId] = record;
            }

            record.Author = message.Author;
            record.Role = message.Role ?? string.Empty;
            record.Metadata = ToMetadata(message.AdditionalProperties);
            record.Contents = message.Contents;
            record.Error = ExtractError(message);
            record.UpdateTime = DateTime.UtcNow;
        }

        await _unitOfWork.SaveChangesAsync();
    }

    private static Dictionary<string, JsonElement>? ToMetadata(AdditionalPropertiesDictionary? additionalProperties)
    {
        if (additionalProperties == null || additionalProperties.Count == 0)
        {
            return null;
        }

        var metadata = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in additionalProperties)
        {
            metadata[key] = JsonSerializer.SerializeToElement(value);
        }

        return metadata;
    }

    private static string? ExtractError(DSystem.Shared.Models.AiMessage message)
    {
        var errorContent = message.Contents
            .FirstOrDefault(content => content.Type == nameof(ErrorContent));

        return errorContent?.Content?.ToString();
    }

    private static void AppendUserInput(List<AgentResponseUpdate> updates, string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        updates.Add(new AgentResponseUpdate
        {
            MessageId = $"user_{Guid.NewGuid():N}",
            Role = ChatRole.User,
            AuthorName = "user",
            Contents = [new TextContent(input.Trim())]
        });
    }
}
