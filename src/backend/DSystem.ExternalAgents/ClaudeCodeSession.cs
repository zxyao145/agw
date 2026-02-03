using ClaudeCodeSdk.MAF;
using DSystem.Domain.Entities;
using DSystem.Domain.Models;
using DSystem.Infrastructure;
using DSystem.Infrastructure.Data;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DSystem.ExternalAgents;

/// <summary>
/// Wraps a ClaudeCode agent to execute WebSocket inputs and build streaming outputs.
/// </summary>
public sealed class ClaudeCodeSession : IAsyncDisposable
{
    private bool _disposed;
    private CancellationTokenSource _cancellationTokenSource = new();

    /// <summary>
    /// Gets the ClaudeCode AI Agent.
    /// </summary>
    public ClaudeCodeAIAgent Agent { get; }

    /// <summary>
    /// Gets the agent thread for conversation context.
    /// </summary>
    public AgentSession Thread { get; private set; } 

    /// <summary>
    /// Gets the session configuration.
    /// </summary>
    public ClaudeCodeSettingRequest Configuration { get; }

    /// <summary>
    /// Gets the cancellation token for in-flight requests.
    /// </summary>
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;

    private readonly ILogger _logger;
    private readonly LlmDbContext _context;

    private const string ClaudeCodeAgentName = "ClaudeCode";

    /// <summary>
    /// Initializes a new instance of the ClaudeCodeSession class.
    /// </summary>
    public ClaudeCodeSession(
        ClaudeCodeAIAgent agent,
        AgentSession thread,
        ClaudeCodeSettingRequest configuration,
        ILogger logger,
        LlmDbContext context)
    {
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Executes a text input with streaming responses.
    /// </summary>
    public async IAsyncEnumerable<AiMessage> ExecuteStreamingAsync(
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var content = new AiMessageInputContent(
            AiMessageContentType.TextContent,
            JsonSerializer.SerializeToElement(input));

        await foreach (var message in ExecuteStreamingAsync([content], cancellationToken))
        {
            yield return message;
        }
    }

    /// <summary>
    /// Executes a list of input contents with streaming responses.
    /// </summary>
    public async IAsyncEnumerable<AiMessage> ExecuteStreamingAsync(
        List<AiMessageInputContent> contents,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var aiContents = ConvertToAIContents(contents);
        var message = new ChatMessage(ChatRole.User, aiContents);
        var responseUpdates = new List<AgentResponseUpdate>();

        await foreach (var update in Agent.RunStreamingAsync(message, Thread, cancellationToken: cancellationToken))
        {
            responseUpdates.Add(update);
            var aiMessage = update.ToAiMessage();
            if (aiMessage != null) yield return aiMessage;
        }

        await SaveThreadStateAsync(responseUpdates, cancellationToken);
    }

    /// <summary>
    /// Updates the thread state (useful for thread deserialization).
    /// </summary>
    public void UpdateThread(AgentSession newThread) => Thread = newThread;

    /// <summary>
    /// Cancels any in-flight request.
    /// </summary>
    public void CancelActiveRequest()
    {
        if (_cancellationTokenSource.IsCancellationRequested) return;
        _cancellationTokenSource.Cancel();
    }

    /// <summary>
    /// Prepares a new cancellation token for a subsequent request.
    /// </summary>
    public void ResetCancellationToken()
    {
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        try
        {
            await Agent.DisposeAsync();
            _cancellationTokenSource.Dispose();
            _logger.LogDebug("ClaudeCodeSession disposed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing ClaudeCodeSession");
        }
        finally
        {
            _disposed = true;
        }
    }

    private static List<AIContent> ConvertToAIContents(List<AiMessageInputContent> contents)
    {
        var aiContents = new List<AIContent>();

        foreach (var item in contents)
        {
            if (item.Type == AiMessageContentType.TextContent)
            {
                aiContents.Add(new TextContent(item.Content.GetString()));
                continue;
            }
            if (item.Type == AiMessageContentType.UriContent)
            {
                var uri = item.Content.GetProperty("uri").GetString() ?? "";
                var mediaType = item.Content.GetProperty("mediaType").GetString() ?? "";
                aiContents.Add(new UriContent(uri, mediaType));
            }
        }

        return aiContents;
    }

    private async Task SaveThreadStateAsync(
        IReadOnlyCollection<AgentResponseUpdate> updates,
        CancellationToken cancellationToken)
    {
        var serialized = Thread.Serialize();
        if (serialized.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;

        var record = await _context.AgentSessionRecords
            .FirstOrDefaultAsync(
                session => session.SessionId == Configuration.SessionId && session.ProjectId == Configuration.ProjectId,
                cancellationToken);

        if (record == null)
        {
            record = new AgentSessionRecord
            {
                SessionId = Configuration.SessionId,
                ProjectId = Configuration.ProjectId,
                CreateTime = DateTime.UtcNow
            };
            await _context.AgentSessionRecords.AddAsync(record);
        }

        var payload = DeserializePayload(record.Messages);
        payload.Thread = serialized;
        if (updates.Count > 0)
        {
            payload.Updates.AddRange(updates);
        }

        record.Messages = JsonSerializer.Serialize(payload);
        record.UpdateTime = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogDebug("Saved thread state for session: {ThreadId}", Configuration.SessionId);
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

    private sealed class SessionRecordPayload
    {
        public JsonElement Thread { get; set; }
        public List<AgentResponseUpdate> Updates { get; set; } = [];
    }
}
