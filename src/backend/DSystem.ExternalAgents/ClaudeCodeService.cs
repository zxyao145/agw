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
    /// Execute ClaudeCode query with streaming responses.
    /// </summary>
    /// <param name="prompt">User prompt to send to ClaudeCode</param>
    /// <param name="workingDirectory">Working directory for ClaudeCode (optional)</param>
    /// <param name="apiKey">Anthropic API key (optional, uses environment variable if not provided)</param>
    /// <param name="apiBaseUrl">Anthropic base URL (optional)</param>
    /// <param name="systemPrompt">System prompt for ClaudeCode (optional)</param>
    /// <param name="maxTurns">Maximum number of turns (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of ClaudeCodeMessage</returns>
    public async IAsyncEnumerable<AiMessage> ExecuteStreamingAsync(
        ClaudeCodeExecuteRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var threadId = request.ThreadId;
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId, nameof(threadId));
        string prompt = request.Input;
        string? workingDirectory = request.WorkingDirectory;
        string? apiKey = request.ApiKey;
        string? apiBaseUrl = request.ApiBaseUrl;
        string? systemPrompt = request.SystemPrompt;
        int? maxTurns = request.MaxTurns;
        PermissionMode? mode = null;
        if (!string.IsNullOrWhiteSpace(request.PermissionMode))
        {
            mode = Enum.Parse<PermissionMode>(request.PermissionMode);
        }
        var options = new ClaudeCodeAIAgentOptions
        {
            WorkingDirectory = workingDirectory,
            SystemPrompt = systemPrompt,
            MaxTurns = maxTurns,
            PermissionMode = mode,
        };
        options.EnvironmentVariables = request.EnvironmentVariables;

        if (!string.IsNullOrEmpty(apiKey))
        {
            options.ApiKey = apiKey;
        }
        if (!string.IsNullOrEmpty(apiBaseUrl))
        {
            options.BaseUrl = apiBaseUrl;
        }

        var aiAgent = new ClaudeCodeAIAgent(options, _logger);

        AgentThread agentThread;
        var value = await _cache.GetOrCreateAsync<string>(threadId, (c) =>
        {
            return ValueTask.FromResult("");
        });
        if (string.IsNullOrWhiteSpace(value))
        {
            agentThread = aiAgent.GetNewThread();
        }
        else
        {
            var serializedThread = JsonSerializer.Deserialize<JsonElement>(value);
            agentThread = aiAgent.DeserializeThread(serializedThread);
        }


        var agentRunResponseUpdate = aiAgent
            .RunStreamingAsync(prompt, agentThread, cancellationToken: cancellationToken);

        await foreach (var update in agentRunResponseUpdate)
        {
            // Convert SDK message to our DTO
            var aiMessage = update.ToAiMessage();
            if (aiMessage != null)
            {
                yield return aiMessage;
            }
        }

        // Save thread state to cache after execution
        var serializeJsonElement = agentThread.Serialize();
        if (serializeJsonElement.ValueKind != JsonValueKind.Undefined
            && serializeJsonElement.ValueKind == JsonValueKind.Null)
        {
            var serialized = JsonSerializer.Serialize(serializeJsonElement);
            await _cache.SetAsync(threadId, serialized, cancellationToken: cancellationToken);
        }
    }
}
