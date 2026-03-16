using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;
using DSystem.Domain.Services;
using DSystem.Shared;
using DSystem.Shared.Enums;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace DSystem.Appliaction.Services;

public class TaskRecordApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IRepository<ProjectTask> _taskRepository;
    private readonly IRepository<TaskRecord> _recordRepository;
    private readonly IUnitOfWork _unitOfWork;

    public TaskRecordApplication(
        IRepository<ProjectTask> taskRepository,
        IRepository<TaskRecord> recordRepository,
        IUnitOfWork unitOfWork)
    {
        _taskRepository = taskRepository;
        _recordRepository = recordRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<bool> HasSessionAsync(
        string sessionId,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var records = await _recordRepository.ListAsync(r => r.SessionId == sessionId);
        if (records.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return true;
        }

        var contexts = records
            .Select(r => r.ContextId)
            .Where(contextId => !string.IsNullOrWhiteSpace(contextId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (contexts.Length == 0)
        {
            return false;
        }

        var tasks = await _taskRepository.ListAsync(t => t.ProjectId == projectId);
        var knownContexts = tasks
            .Select(t => t.ContextId)
            .ToHashSet(StringComparer.Ordinal);

        return contexts.Any(knownContexts.Contains);
    }

    private static string NormalizeTitle(string? title, string? input)
    {
        var normalizedTitle = NormalizeOptionalText(title);
        if (!string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return normalizedTitle;
        }

        var normalizedInput = NormalizeOptionalText(input);
        if (string.IsNullOrWhiteSpace(normalizedInput))
        {
            return "New Chat";
        }

        return normalizedInput.Length <= 80
            ? normalizedInput
            : normalizedInput[..80];
    }

    private static string NormalizeDescription(string? description, string? input) =>
        NormalizeOptionalText(description)
        ?? NormalizeOptionalText(input)
        ?? string.Empty;

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TaskRecord CreateRecord(
        string contextId,
        string sessionId,
        string? agentName,
        ChatMessage message,
        long sequence,
        DateTime now)
    {
        return new TaskRecord
        {
            Id = Guid.NewGuid(),
            ContextId = contextId,
            SessionId = sessionId,
            AgentName = agentName,
            ConversationSequence = sequence,
            ConversationPayload = JsonSerializer.Serialize(message, JsonOptions),
            CreateTime = now,
            UpdateTime = now
        };
    }

    private static ChatMessage? CreateChatMessage(AgentResponseUpdate update)
    {
        if (!update.Role.HasValue)
        {
            return null;
        }

        return new ChatMessage(update.Role.Value, update.Contents)
        {
            AuthorName = update.AuthorName,
            MessageId = update.MessageId,
            AdditionalProperties = update.AdditionalProperties
        };
    }
}
