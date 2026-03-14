using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;
using DSystem.Shared;
using DSystem.Shared.Enums;
using DSystem.Shared.Models;
using Microsoft.Agents.AI;

namespace DSystem.Appliaction.Services;

public class TaskRecordApplication
{
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

    public async Task SaveThreadStateAsync(
        string sessionId,
        string contextId,
        string? projectId,
        ProjectTaskAgentType agentType,
        Guid? agentId,
        string? agentName,
        IReadOnlyCollection<AgentResponseUpdate> updates,
        string? input,
        string? title = null,
        string? description = null,
        string? systemPrompt = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(contextId)
            || string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        var messages = updates
            .Select(update => update.ToAiMessage())
            .Where(message => message != null)
            .Cast<AiMessage>()
            .ToList();

        if (messages.Count == 0 && string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var task = await _taskRepository.SingleOrDefaultAsync(
            t => t.ContextId == contextId,
            cancellationToken);

        if (task == null)
        {
            task = new ProjectTask
            {
                Id = Guid.TryParse(contextId, out var taskId) ? taskId : Guid.NewGuid(),
                ProjectId = projectId.Trim(),
                ContextId = contextId,
                AgentType = agentType,
                AgentId = agentId,
                Title = NormalizeTitle(title, input),
                Description = NormalizeDescription(description, input),
                SystemPrompt = NormalizeOptionalText(systemPrompt),
                Status = ProjectTaskStatus.Succeeded,
                FinishedTime = now,
                CreateBy = "system",
                CreateTime = now,
                UpdateBy = "system",
                UpdateTime = now
            };

            await _taskRepository.AddAsync(task);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(task.ProjectId))
            {
                task.ProjectId = projectId.Trim();
            }

            if (string.IsNullOrWhiteSpace(task.Title) || task.Title is "Untitled" or "New Chat")
            {
                task.Title = NormalizeTitle(title, input);
            }

            if (string.IsNullOrWhiteSpace(task.Description))
            {
                task.Description = NormalizeDescription(description, input);
            }

            if (string.IsNullOrWhiteSpace(task.SystemPrompt))
            {
                task.SystemPrompt = NormalizeOptionalText(systemPrompt);
            }

            task.AgentType = agentType;
            task.AgentId = agentId;

            task.UpdateBy = "system";
            task.UpdateTime = now;
            _taskRepository.Update(task);
        }

        var existingRecords = await _recordRepository.ListAsync(
            r => r.ContextId == contextId && r.SessionId == sessionId);
        var placeholderRecord = existingRecords
            .OrderByDescending(r => r.UpdateTime ?? r.CreateTime)
            .ThenByDescending(r => r.CreateTime)
            .FirstOrDefault(r => r.Messages.Count == 0);

        if (placeholderRecord != null)
        {
            placeholderRecord.AgentName = NormalizeOptionalText(agentName);
            placeholderRecord.Messages = messages;
            placeholderRecord.Error = ExtractError(messages);
            placeholderRecord.UpdateBy = "system";
            placeholderRecord.UpdateTime = now;
            _recordRepository.Update(placeholderRecord);
        }
        else
        {
            var record = new TaskRecord
            {
                Id = Guid.NewGuid(),
                ContextId = contextId,
                SessionId = sessionId,
                AgentName = NormalizeOptionalText(agentName),
                Input = CreateInputMessage(input),
                Messages = messages,
                Metadata = [],
                Error = ExtractError(messages),
                CreateBy = "system",
                CreateTime = now,
                UpdateBy = "system",
                UpdateTime = now
            };

            await _recordRepository.AddAsync(record);
        }

        await _unitOfWork.SaveChangesAsync();
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

    private static UserInputMessage CreateInputMessage(string? input)
    {
        var trimmed = input?.Trim() ?? string.Empty;
        return new UserInputMessage(
            [new AiMessageContent(AiMessageContentType.TextContent, trimmed)]);
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

    private static string? ExtractError(IEnumerable<AiMessage> messages)
    {
        return messages
            .SelectMany(message => message.Contents)
            .FirstOrDefault(content => content.Type == AiMessageContentType.ErrorContent)
            ?.Content
            ?.ToString();
    }
}
