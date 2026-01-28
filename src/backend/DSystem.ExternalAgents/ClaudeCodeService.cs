using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;
using DSystem.Domain.Models;
using DSystem.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DSystem.ExternalAgents;

/// <summary>
/// Service for executing ClaudeCode queries with streaming support.
/// </summary>
public class ClaudeCodeService
{
    private readonly ILogger<ClaudeCodeService> _logger;
    private readonly HybridCache _cache;

    public ClaudeCodeService(ILogger<ClaudeCodeService> logger, HybridCache cache)
    {
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// Initialize a new ClaudeCode session with the specified configuration.
    /// </summary>
    public async Task<ClaudeCodeSession> InitializeSessionAsync(
        ClaudeCodeSettingRequest initRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initRequest.SessionId);

        var options = BuildAgentOptions(initRequest);
        var agent = new ClaudeCodeAIAgent(options, _logger);
        var thread = await GetOrLoadThreadAsync(agent, initRequest.SessionId, cancellationToken);

        return new ClaudeCodeSession(agent, thread, initRequest, _logger);
    }

    /// <summary>
    /// Execute ClaudeCode query with streaming responses using an existing session.
    /// </summary>
    public async IAsyncEnumerable<AiMessage> ExecuteSessionStreamingAsync(
        ClaudeCodeSession session,
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var content = new AiMessageInputContent(
            AiMessageContentType.TextContent,
            JsonSerializer.SerializeToElement(input));

        await foreach (var message in ExecuteSessionStreamingAsync(session, [content], cancellationToken))
        {
            yield return message;
        }
    }

    /// <summary>
    /// Execute ClaudeCode query with streaming responses using an existing session.
    /// </summary>
    public async IAsyncEnumerable<AiMessage> ExecuteSessionStreamingAsync(
        ClaudeCodeSession session,
        List<AiMessageInputContent> contents,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var aiContents = ConvertToAIContents(contents);
        var message = new ChatMessage(ChatRole.User, aiContents);

        await foreach (var update in session.Agent.RunStreamingAsync(message, session.Thread, cancellationToken: cancellationToken))
        {
            var aiMessage = update.ToAiMessage();
            if (aiMessage != null) yield return aiMessage;
        }

        await SaveThreadStateAsync(session, cancellationToken);
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

    private async Task<AgentThread> GetOrLoadThreadAsync(
        ClaudeCodeAIAgent agent,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var cachedThread = await _cache.GetOrCreateAsync(
            sessionId,
            _ => ValueTask.FromResult<string>(""));

        if (string.IsNullOrWhiteSpace(cachedThread))
        {
            _logger.LogDebug("Created new thread for session: {ThreadId}", sessionId);
            return await agent.GetNewThreadAsync(cancellationToken);
        }

        _logger.LogDebug("Loaded existing thread for session: {ThreadId}", sessionId);
        var serialized = JsonSerializer.Deserialize<JsonElement>(cachedThread);
        return await agent.DeserializeThreadAsync(serialized);
    }

    private static ClaudeCodeAIAgentOptions BuildAgentOptions(ClaudeCodeSettingRequest request)
    {
        PermissionMode? permissionMode = null;
        if (!string.IsNullOrWhiteSpace(request.PermissionMode))
            permissionMode = Enum.Parse<PermissionMode>(request.PermissionMode);

        var options = new ClaudeCodeAIAgentOptions
        {
            WorkingDirectory = request.WorkingDirectory,
            SystemPrompt = request.SystemPrompt,
            MaxTurns = request.MaxTurns,
            EnvironmentVariables = request.EnvironmentVariables,
            PermissionMode = permissionMode
        };

        if (!string.IsNullOrEmpty(request.ApiKey)) options.ApiKey = request.ApiKey;
        if (!string.IsNullOrEmpty(request.ApiBaseUrl)) options.BaseUrl = request.ApiBaseUrl;

        return options;
    }

    private async Task SaveThreadStateAsync(ClaudeCodeSession session, CancellationToken cancellationToken)
    {
        var serialized = session.Thread.Serialize();
        if (serialized.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;

        await _cache.SetAsync(
            session.Configuration.SessionId,
            JsonSerializer.Serialize(serialized),
            cancellationToken: cancellationToken);

        _logger.LogDebug("Saved thread state for session: {ThreadId}", session.Configuration.SessionId);
    }
}
