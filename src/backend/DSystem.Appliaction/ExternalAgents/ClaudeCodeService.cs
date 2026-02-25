using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;
using DSystem.SessionRecords.Application;
using DSystem.SessionRecords.Entities;
using DSystem.SessionRecords.Repositories;
using DSystem.Shared.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DSystem.Appliaction.ExternalAgents;

/// <summary>
/// Factory for creating ClaudeCode sessions.
/// </summary>
public class ClaudeCodeService
{
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<ClaudeCodeService> _logger;
    private readonly IAgentSessionRecordRepository _repository;
    private readonly SessionRecordApplication _sessionRecordApplication;
    private readonly IGitCommandService _gitCommandService;
    private readonly string _rootPath;

    public ClaudeCodeService(
        ILogger<ClaudeCodeService> logger,
        IAgentSessionRecordRepository repository,
        IHostEnvironment hostEnvironment,
        IGitCommandService gitCommandService,
        SessionRecordApplication sessionRecordApplication)
    {
        _logger = logger;
        _repository = repository;
        _sessionRecordApplication = sessionRecordApplication;
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
    public async Task<AgentExecSession> InitializeSessionAsync(
        ClaudeCodeSettingRequest initRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initRequest.SessionId);

        await EnsureGitRepositoryAsync(initRequest, cancellationToken);

        var record = await FindSessionRecordAsync(initRequest.SessionId, initRequest.ProjectId);
        var options = BuildAgentOptions(initRequest, hasSessionRecord: record != null);
        var agent = new ClaudeCodeAIAgent(options, _logger);
        var thread = await GetOrLoadThreadAsync(
            agent,
            initRequest.SessionId,
            record,
            cancellationToken);

        return new AgentExecSession(
            agent,
            thread,
            initRequest.ProjectId,
            initRequest.SessionId,
            _logger,
            _sessionRecordApplication
            );
    }

    private async Task<AgentSession> GetOrLoadThreadAsync(
        ClaudeCodeAIAgent agent,
        string sessionId,
        AgentSessionRecord? record,
        CancellationToken cancellationToken)
    {
        if (record == null)
        {
            _logger.LogDebug("Created new thread for session: {SessionId}", sessionId);
            return await agent.CreateSessionAsync(cancellationToken);
        }
        _logger.LogDebug("Loaded existing thread for session: {SessionId}", sessionId);
        //if (!TryGetThreadState(record.Contents, out var threadState))
        //{
        //    return await agent.CreateSessionAsync(cancellationToken);
        //}

        JsonElement threadState = JsonElement.Parse($"\"sessionId\": {sessionId}");

        return await agent.DeserializeSessionAsync(threadState);
    }

    /// <summary>
    /// Tries to extract serialized thread state from stored session payload.
    /// </summary>
    private static bool TryGetThreadState(string messages, out JsonElement threadState)
    {
        threadState = default;
        if (string.IsNullOrWhiteSpace(messages))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(messages);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("Thread", out var threadElement)
                    || document.RootElement.TryGetProperty("thread", out threadElement))
                {
                    threadState = threadElement.Clone();
                    return threadState.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
                }
            }

            threadState = document.RootElement.Clone();
            return threadState.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ClaudeCodeAIAgentOptions BuildAgentOptions(ClaudeCodeSettingRequest request, bool hasSessionRecord)
    {
        PermissionMode? permissionMode = null;
        if (!string.IsNullOrWhiteSpace(request.PermissionMode))
            permissionMode = Enum.Parse<PermissionMode>(request.PermissionMode);
        var resume = hasSessionRecord ? request.SessionId : null;
        var optionSessionId = hasSessionRecord ? (Guid?)null : Guid.Parse(request.SessionId);

        var options = new ClaudeCodeAIAgentOptions
        {
            WorkingDirectory = request.WorkingDirectory,
            SystemPrompt = request.SystemPrompt,
            MaxTurns = request.MaxTurns,
            EnvironmentVariables = request.EnvironmentVariables,
            PermissionMode = permissionMode,
            Resume = resume,
            SessionId = optionSessionId
        };

        if (!string.IsNullOrEmpty(request.ApiKey)) options.ApiKey = request.ApiKey;
        if (!string.IsNullOrEmpty(request.ApiBaseUrl)) options.BaseUrl = request.ApiBaseUrl;

        return options;
    }

    private async Task<AgentSessionRecord?> FindSessionRecordAsync(string sessionId, string projectId)
    {
        var records = await _repository.ListAsync(session => session.SessionId == sessionId && session.ProjectId == projectId);
        return records.FirstOrDefault();
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
