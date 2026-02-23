using DSystem.SessionRecords.Domain;
using DSystem.Shared;
using DSystem.Shared.Contracts;
using DSystem.Shared.Models;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace DSystem.Api.Controllers;

[ApiController]
[Route("api/session-records")]
public class SessionRecordsController : ControllerBase
{
    private readonly SessionRecordDomainService _service;

    public SessionRecordsController(SessionRecordDomainService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync([FromQuery] string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return BadRequest("projectId is required.");
        }

        var records = await _service.ListAsync(r => r.ProjectId == projectId);
        var summaries = records
            .OrderByDescending(r => r.UpdateTime ?? r.CreateTime)
            .Select(r => new SessionRecordSummary(
                r.Id,
                r.ProjectId,
                r.SessionId,
                NormalizeTitle(r.Title),
                ExtractMessageCount(r.Messages),
                r.CreateTime,
                r.UpdateTime))
            .ToList();

        return Ok(summaries);
    }

    [HttpGet("{sessionId}")]
    public async Task<IActionResult> GetAsync(string sessionId, [FromQuery] string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return BadRequest("projectId is required.");
        }

        var record = await _service.GetBySessionIdAsync(sessionId, projectId);
        if (record == null)
        {
            return NotFound();
        }

        var messages = ExtractMessages(record.Messages);
        var response = new SessionRecordDetails(
            record.Id,
            record.ProjectId,
            record.SessionId,
            NormalizeTitle(record.Title),
            messages,
            record.CreateTime,
            record.UpdateTime);

        return Ok(response);
    }

    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> DeleteAsync(string sessionId, [FromQuery] string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return BadRequest("projectId is required.");
        }

        var deleted = await _service.DeleteBySessionIdAsync(sessionId, projectId);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPut("{sessionId}/title")]
    public async Task<IActionResult> UpdateTitleAsync(
        string sessionId,
        [FromQuery] string projectId,
        [FromBody] SessionRecordTitleUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return BadRequest("projectId is required.");
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest("Title is required.");
        }

        var user = User?.Identity?.Name ?? "system";
        var updated = await _service.UpdateTitleAsync(sessionId, projectId, request.Title, user);
        return updated ? NoContent() : NotFound();
    }

    private static int ExtractMessageCount(string? messagesPayload) =>
        ExtractUpdates(messagesPayload).Count;

    private static List<AiMessage> ExtractMessages(string? messagesPayload)
    {
        var updates = ExtractUpdates(messagesPayload);
        return updates
            .Select(update => update.ToAiMessage())
            .Where(message => message != null)
            .Select(message => message!)
            .ToList();
    }

    private static List<AgentResponseUpdate> ExtractUpdates(string? messagesPayload)
    {
        if (string.IsNullOrWhiteSpace(messagesPayload))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(messagesPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            if (!TryGetUpdates(document.RootElement, out var updatesElement))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<AgentResponseUpdate>>(updatesElement.GetRawText())
                ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TryGetUpdates(JsonElement root, out JsonElement updatesElement)
    {
        if (root.TryGetProperty("Updates", out updatesElement)
            || root.TryGetProperty("updates", out updatesElement))
        {
            return updatesElement.ValueKind == JsonValueKind.Array;
        }

        updatesElement = default;
        return false;
    }

    private static string NormalizeTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "New Chat" : title;
}
