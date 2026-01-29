using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;
using DSystem.Domain.Models;
using DSystem.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DSystem.ExternalAgents;

/// <summary>
/// Service for executing ClaudeCode queries with streaming support.
/// </summary>
public class ClaudeCodeService
{
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<ClaudeCodeService> _logger;
    private readonly HybridCache _cache;
    private readonly string _rootPath;

    public ClaudeCodeService(ILogger<ClaudeCodeService> logger, HybridCache cache, IHostEnvironment hostEnvironment)
    {
        _logger = logger;
        _cache = cache;
        _hostEnvironment = hostEnvironment;
        _rootPath = Path.Combine(_hostEnvironment.ContentRootPath);
        if (!Directory.Exists(_rootPath))
        {
            Directory.CreateDirectory(_rootPath);
        }
    }

    /// <summary>
    /// Initialize a new ClaudeCode session with the specified configuration.
    /// </summary>
    public async Task<ClaudeCodeSession> InitializeSessionAsync(
        ClaudeCodeSettingRequest initRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initRequest.SessionId);

        await EnsureGitRepositoryAsync(initRequest, cancellationToken);

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

    private async Task EnsureGitRepositoryAsync(ClaudeCodeSettingRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.GitAddress))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            throw new InvalidOperationException("Working directory is required when Git address is provided.");
        }

        var resolvedWorkingDirectory = Path.Combine(_rootPath, request.WorkingDirectory);
        var gitMetadataPath = Path.Combine(resolvedWorkingDirectory, ".git");
        if (Directory.Exists(gitMetadataPath))
        {
            return;
        }

        var createdDirectory = false;
        if (!Directory.Exists(resolvedWorkingDirectory))
        {
            Directory.CreateDirectory(resolvedWorkingDirectory);
            createdDirectory = true;
        }
        else if (Directory.EnumerateFileSystemEntries(resolvedWorkingDirectory).Any())
        {
            throw new InvalidOperationException(
                $"Working directory '{resolvedWorkingDirectory}' already exists and is not empty, but no git repository was found.");
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"clone {request.GitAddress} .",
                WorkingDirectory = resolvedWorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            _logger.LogError(
                "Failed to clone git repository {GitAddress} into {WorkingDirectory}. Stdout: {Stdout}. Stderr: {Stderr}",
                request.GitAddress,
                resolvedWorkingDirectory,
                stdout,
                stderr);
            if (createdDirectory)
            {
                try
                {
                    Directory.Delete(resolvedWorkingDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to clean up working directory {WorkingDirectory} after clone failure.",
                        resolvedWorkingDirectory);
                }
            }
            throw new InvalidOperationException("Failed to clone git repository. See logs for details.");
        }

        _logger.LogInformation(
            "Cloned git repository {GitAddress} into {WorkingDirectory}",
            request.GitAddress,
            resolvedWorkingDirectory);
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
