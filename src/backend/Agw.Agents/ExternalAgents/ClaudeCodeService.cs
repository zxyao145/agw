using Agw.Agents.Application;
using Agw.Appliaction.ExternalAgents;
using Agw.Shared.Enums;
using Agw.Shared.Services;
using Agw.Shared.Tasks;
using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.ExternalAgents;

public class ClaudeCodeService
{
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<ClaudeCodeService> _logger;
    private readonly ITaskAppService _taskRecordAppService;
    private readonly ChatHistoryProvider _chatHistoryProvider;
    private readonly IProviderSessionState _providerSessionState;
    private readonly IGitCommandService _gitCommandService;
    private readonly string _rootPath;

    public ClaudeCodeService(
        ILogger<ClaudeCodeService> logger,
        IHostEnvironment hostEnvironment,
        IGitCommandService gitCommandService,
        ChatHistoryProvider chatHistoryProvider,
        IProviderSessionState providerSessionState,
        ITaskAppService taskRecordAppService)
    {
        _logger = logger;
        _taskRecordAppService = taskRecordAppService;
        _hostEnvironment = hostEnvironment;
        _chatHistoryProvider = chatHistoryProvider;
        _gitCommandService = gitCommandService;
        _rootPath = Path.Combine(_hostEnvironment.ContentRootPath);
        if (!Directory.Exists(_rootPath))
        {
            Directory.CreateDirectory(_rootPath);
        }

        _providerSessionState = providerSessionState;
    }

    public async Task<AgentExecSession> InitializeSessionAsync(
        ClaudeCodeSettingRequest initRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initRequest.SessionId);

        await EnsureGitRepositoryAsync(initRequest, cancellationToken);
        var projectId = ProjectDefaults.ClaudeCodeId;
        var hasTaskRecord = await _taskRecordAppService.HasSessionAsync(
            initRequest.SessionId,
            projectId,
            cancellationToken);

        var options = BuildAgentOptions(initRequest, hasTaskRecord);
        var agent = new ClaudeCodeAIAgent(options, _logger);
        var agentSession = await GetOrCreateAgentSessionAsync(
            agent,
            initRequest.SessionId,
            hasTaskRecord,
            cancellationToken);

        _providerSessionState.InitializeSessionState(
            agentSession,
            initRequest.SessionId,
            initRequest.SessionId,
            projectId);

        return new AgentExecSession(
            agent,
            agentSession,
            projectId,
            initRequest.SessionId,
            initRequest.SessionId,
            AgentRuntimeType.Agent,
            null,
            "ClaudeCode",
            _logger,
            taskTitle: "New Chat",
            systemPrompt: initRequest.SystemPrompt);
    }

    private async Task<AgentSession> GetOrCreateAgentSessionAsync(
        ClaudeCodeAIAgent agent,
        string sessionId,
        bool hasTaskRecord,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            hasTaskRecord
                ? "Resuming thread for session: {SessionId}"
                : "Creating new thread for session: {SessionId}",
            sessionId);

        return await agent.CreateSessionAsync(cancellationToken);
    }

    private ClaudeCodeAIAgentOptions BuildAgentOptions(ClaudeCodeSettingRequest request, bool hasTaskRecord)
    {
        PermissionMode? permissionMode = null;
        if (!string.IsNullOrWhiteSpace(request.PermissionMode))
        {
            permissionMode = Enum.Parse<PermissionMode>(request.PermissionMode);
        }

        var resume = hasTaskRecord ? request.SessionId : null;
        var optionSessionId = hasTaskRecord ? (Guid?)null : Guid.Parse(request.SessionId);
        var options = new ClaudeCodeAIAgentOptions
        {
            WorkingDirectory = request.WorkingDirectory,
            SystemPrompt = request.SystemPrompt,
            MaxTurns = request.MaxTurns,
            EnvironmentVariables = request.EnvironmentVariables,
            PermissionMode = permissionMode,
            Resume = resume,
            SessionId = optionSessionId,
            ChatHistoryProvider = _chatHistoryProvider
        };

        if (!string.IsNullOrEmpty(request.ApiKey))
        {
            options.ApiKey = request.ApiKey;
        }

        if (!string.IsNullOrEmpty(request.ApiBaseUrl))
        {
            options.BaseUrl = request.ApiBaseUrl;
        }

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

        if (cloneResult.Success)
        {
            _logger.LogInformation(
                "Cloned git repository {GitAddress} into {WorkingDirectory}",
                request.GitAddress,
                resolvedWorkingDirectory);
            return;
        }

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
}
