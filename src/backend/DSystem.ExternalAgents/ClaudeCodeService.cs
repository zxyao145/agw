using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;
using DSystem.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DSystem.ExternalAgents;

/// <summary>
/// Factory for creating ClaudeCode sessions.
/// </summary>
public class ClaudeCodeService
{
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<ClaudeCodeService> _logger;
    private readonly HybridCache _cache;
    private readonly IGitCommandService _gitCommandService;
    private readonly string _rootPath;

    public ClaudeCodeService(
        ILogger<ClaudeCodeService> logger,
        HybridCache cache,
        IHostEnvironment hostEnvironment,
        IGitCommandService gitCommandService)
    {
        _logger = logger;
        _cache = cache;
        _hostEnvironment = hostEnvironment;
        _gitCommandService = gitCommandService;
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

        return new ClaudeCodeSession(agent, thread, initRequest, _logger, _cache);
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

        var cloneResult = await _gitCommandService.CloneRepositoryAsync(
            request.GitAddress,
            resolvedWorkingDirectory,
            cancellationToken);

        if (!cloneResult.Success)
        {
            _logger.LogError(
                "Failed to clone git repository {GitAddress} into {WorkingDirectory}. Stdout: {Stdout}. Stderr: {Stderr}",
                request.GitAddress,
                resolvedWorkingDirectory,
                cloneResult.Stdout,
                cloneResult.Stderr);
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
}
