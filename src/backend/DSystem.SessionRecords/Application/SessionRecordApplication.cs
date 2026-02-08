using DSystem.SessionRecords.Entities;
using DSystem.SessionRecords.Repositories;
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
        Guid projectId,
        JsonElement serializedThread,
        IReadOnlyCollection<AgentResponseUpdate> updates,
        string? input,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        if (serializedThread.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var records = await _repository.ListAsync(session =>
            session.SessionId == sessionId && session.ProjectId == projectId);
        var record = records.FirstOrDefault();

        if (record == null)
        {
            record = new AgentSessionRecord
            {
                SessionId = sessionId,
                ProjectId = projectId,
                CreateTime = DateTime.UtcNow
            };
            await _repository.AddAsync(record);
        }

        if (string.IsNullOrWhiteSpace(record.Title))
        {
            var title = GenerateTitleFromInput(input);
            if (!string.IsNullOrWhiteSpace(title))
            {
                record.Title = title;
            }
        }

        var payload = DeserializePayload(record.Messages);
        payload.Thread = serializedThread.Clone();
        AppendUserInput(payload, input);
        if (updates.Count > 0)
        {
            payload.Updates.AddRange(updates);
        }

        record.Messages = JsonSerializer.Serialize(payload);
        record.UpdateTime = DateTime.UtcNow;

        await _unitOfWork.SaveChangesAsync();
    }

    private static string? GenerateTitleFromInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var trimmed = input.Trim();

        var firstLine = trimmed.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return null;
        }

        const int maxLength = 20;
        return firstLine.Length > maxLength ? $"{firstLine[..maxLength]}..." : firstLine;
    }

    private static SessionRecordPayload DeserializePayload(string messages)
    {
        if (string.IsNullOrWhiteSpace(messages))
        {
            return new SessionRecordPayload();
        }

        try
        {
            using var document = JsonDocument.Parse(messages);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new SessionRecordPayload { Thread = document.RootElement.Clone() };
            }

            if (!TryGetThreadState(document.RootElement, out var threadState))
            {
                return new SessionRecordPayload { Thread = document.RootElement.Clone() };
            }

            var payload = new SessionRecordPayload { Thread = threadState };
            if (document.RootElement.TryGetProperty("Updates", out var updatesElement)
                && updatesElement.ValueKind == JsonValueKind.Array)
            {
                payload.Updates = JsonSerializer.Deserialize<List<AgentResponseUpdate>>(updatesElement.GetRawText()) ?? [];
            }

            return payload;
        }
        catch (JsonException)
        {
            return new SessionRecordPayload();
        }
    }

    private static bool TryGetThreadState(JsonElement root, out JsonElement threadState)
    {
        if (root.TryGetProperty("Thread", out threadState) || root.TryGetProperty("thread", out threadState))
        {
            threadState = threadState.Clone();
            return threadState.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
        }

        threadState = default;
        return false;
    }

    private static void AppendUserInput(SessionRecordPayload payload, string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        var trimmed = input.Trim();
        payload.Updates.Add(new AgentResponseUpdate
        {
            Role = ChatRole.User,
            AuthorName = "user",
            Contents = [new TextContent(trimmed)]
        });
    }

    private sealed class SessionRecordPayload
    {
        public JsonElement Thread { get; set; }

        public List<AgentResponseUpdate> Updates { get; set; } = [];
    }
}
