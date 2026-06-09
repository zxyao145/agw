using System.ClientModel;
using System.Diagnostics.CodeAnalysis;

using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Application.Agents;
using Agw.Agents.ExternalAgents;
using Agw.Providers.Domain.Entities;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;

using Anthropic;
using Anthropic.Core;

using ClaudeCodeSdk.MAF;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;

using OpenAI;
using OpenAI.Chat;
using OpenAI.CodexSdk.MAF;

namespace Agw.Agents.Application.AgentRun;

public partial class AgentRuntimeService
{
    public async Task<AIAgent?> CreateAiAgentAsync(
        Guid agentId,
        string? extraOverride = null,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentAppService.GetAgentAsync(agentId);
        if (agent == null)
        {
            return null;
        }

        return await CreateAiAgentAsync(new CreateAiAgentRequest
        {
            Agent = agent,
            ExtraOverride = extraOverride,
        }, cancellationToken);
    }

    private async Task<AIAgent?> CreateAiAgentAsync(CreateAiAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Agent);

        if (request.Agent.Type == AgentType.External)
        {
            TryCreateExternalAgent(request, out var externalAgent);
            return externalAgent;
        }

        return await CreateDefinitionAgentAsync(request.Agent, request.Workspace, request.ProjectId, cancellationToken)
            .ConfigureAwait(false);
    }

    #region CreateExternalAgent

    private bool TryCreateExternalAgent(CreateAiAgentRequest request, [NotNullWhen(true)] out AIAgent? aiAgent)
    {
        aiAgent = null;
        if (request.Agent.Type != AgentType.External)
        {
            return false;
        }

        var extra = request.ExtraOverride ?? request.Agent.Extra;
        aiAgent = request.Agent.Name switch
        {
            AgentNames.ClaudeCode => CreateClaudeCodeAgent(
                extra,
                request.Workspace,
                request.TaskId,
                request.Resume,
                request.EnvironmentVariables),
            AgentNames.Codex => CreateCodexAgent(
                extra,
                request.Workspace,
                request.ProviderSessionId,
                request.Resume,
                request.EnvironmentVariables,
                request.OnExternalSessionStartedAsync),
            _ => null
        };
        return aiAgent != null;
    }

    private AIAgent? CreateClaudeCodeAgent(
        string? extra,
        string? workspace,
        Guid? taskId,
        bool resume,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        if (string.IsNullOrWhiteSpace(extra))
        {
            _logger.LogError("agent.Extra is null or whitespace");
            return null;
        }

        var options = JsonUtil.Deserialize<ClaudeCodeAIAgentOptions>(extra);
        if (options == null)
        {
            _logger.LogError("agent.Extra Deserialize to options error");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(workspace))
        {
            options = options with { WorkingDirectory = workspace };
        }

        if (taskId != null)
        {
            options = resume
                ? options with { Resume = taskId.Value.Normalize(), SessionId = null }
                : options with { Resume = null, SessionId = taskId };
        }

        options = AgentRuntimeServiceUtil.ApplyEnvironmentVariables(options, environmentVariables);
        options = options with { ChatHistoryProvider = _chatHistoryProvider };
        return new ClaudeCodeAIAgent(options, _logger);
    }

    private AIAgent? CreateCodexAgent(
        string? extra,
        string? workspace,
        Guid? threadId,
        bool resume,
        IReadOnlyDictionary<string, string>? environmentVariables,
        Func<string, CancellationToken, ValueTask>? onThreadStartedAsync)
    {
        if (string.IsNullOrWhiteSpace(extra))
        {
            _logger.LogError("agent.Extra is null or whitespace");
            return null;
        }

        var options = AgentRuntimeServiceUtil.BuildCodexAIAgentOptions(
            extra,
            workspace,
            threadId,
            resume,
            environmentVariables,
            onThreadStartedAsync);
        if (options == null)
        {
            _logger.LogError("agent.Extra Deserialize to options error");
            return null;
        }

        options = options with { ChatHistoryProvider = _chatHistoryProvider };
        return new CodexAIAgent(options, _logger);
    }

    #endregion


    #region DefinitionAgent

    private async Task<AIAgent?> CreateDefinitionAgentAsync(Agent agentDefinition, string? workspace, Guid? projectId,
        CancellationToken cancellationToken)
    {
        if (!agentDefinition.ModelProviderId.HasValue)
        {
            return null;
        }

        var runtimeConfiguration =
            await _agentAppService.GetModelRuntimeConfigurationAsync(agentDefinition.ModelProviderId.Value);
        if (runtimeConfiguration == null)
        {
            return null;
        }

        var model = runtimeConfiguration.Model;
        var provider = runtimeConfiguration.Provider;
        var authConfigs = provider.AuthConfigs.Where(x => x.Enable).ToList();
        if (authConfigs.Count == 0)
        {
            _logger.LogError("no auth config for provider:{ProviderName}", provider.Name);
            return null;
        }

        var authConfig = authConfigs[Random.Shared.Next(authConfigs.Count)];
        IList<AITool>? tools =
            await CreateAgentTools(agentDefinition, projectId, cancellationToken).ConfigureAwait(false);
        var skillsProvider = await CreateSkillsProviderAsync(agentDefinition.Id).ConfigureAwait(false);

        return provider.ProviderType switch
        {
            ProviderType.OpenAI => CreateOpenAiAgent(agentDefinition, model, provider, authConfig, tools,
                skillsProvider, workspace),
            ProviderType.Anthropic => CreateAnthropicAgent(agentDefinition, model, provider, authConfig, tools,
                skillsProvider, workspace),
            _ => throw new AgwException(ErrorCodes.UnsupportedProviderType,
                $"Provider type '{provider.ProviderType}' is not supported")
        };
    }

    private AIAgent CreateOpenAiAgent(
        Agent agentDefinition,
        LlmModel model,
        Provider provider,
        ProviderAuthConfig authConfig,
        IList<AITool>? tools,
        AIContextProvider? skillsProvider,
        string? workspace)
    {
        var apiKey = ResolveApiKey(authConfig);
        var credential = new ApiKeyCredential(apiKey);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.Endpoint),
        };
        var client = new OpenAIClient(credential, options);
        var chatCompletionClient = client.GetChatClient(model.Name);
        var agentOptions = new ChatClientAgentOptions
        {
            Name = agentDefinition.Name,
            Description = agentDefinition.Description,
            ChatHistoryProvider = _chatHistoryProvider,
            ChatOptions = new ChatOptions
            {
                ModelId = model.Name,
                Instructions = AgentRuntimeServiceUtil.BuildInstructions(agentDefinition.SystemPrompt, workspace),
                Tools = tools
            }
        };

        if (skillsProvider != null)
        {
            agentOptions.AIContextProviders = [skillsProvider];
        }

        return chatCompletionClient.AsAIAgent(agentOptions)
            .AsBuilder()
            .UseOpenTelemetry(sourceName: provider.Name, configure: cfg => cfg.EnableSensitiveData = true)
            .Build();
    }

    private AIAgent CreateAnthropicAgent(
        Agent agentDefinition,
        LlmModel model,
        Provider provider,
        ProviderAuthConfig authConfig,
        IList<AITool>? tools,
        AIContextProvider? skillsProvider,
        string? workspace)
    {
        var anthropicClientOptions = new ClientOptions
        {
            ApiKey = ResolveApiKey(authConfig),
            BaseUrl = provider.Endpoint
        };
        var client = new AnthropicClient(anthropicClientOptions);
        var agentOptions = new ChatClientAgentOptions
        {
            Name = agentDefinition.Name,
            Description = agentDefinition.Description,
            ChatHistoryProvider = _chatHistoryProvider,
            ChatOptions = new ChatOptions
            {
                ModelId = model.Name,
                Instructions = AgentRuntimeServiceUtil.BuildInstructions(agentDefinition.SystemPrompt, workspace),
                Tools = tools
            }
        };

        if (skillsProvider != null)
        {
            agentOptions.AIContextProviders = [skillsProvider];
        }

        return client.AsAIAgent(agentOptions)
            .AsBuilder()
            .UseOpenTelemetry(sourceName: provider.Name, configure: cfg => cfg.EnableSensitiveData = true)
            .Build();
    }

    private string ResolveApiKey(ProviderAuthConfig authConfig)
    {
        if (authConfig.AuthType == ProviderAuthType.ApiKey)
        {
            return authConfig.ApiKey!;
        }

        var envVariableName = authConfig.EnvName!;
        var apiKeyFromEnv = Environment.GetEnvironmentVariable(envVariableName);
        if (string.IsNullOrWhiteSpace(apiKeyFromEnv))
        {
            _logger.LogError("Environment variable '{EnvName}' is not set or empty.", envVariableName);
            throw new AgwException(ErrorCodes.EnvironmentVariableNotSet,
                $"Environment variable '{envVariableName}' is not set or empty.");
        }

        return apiKeyFromEnv;
    }

    private async Task<IList<AITool>?> CreateAgentTools(Agent agent, Guid? projectId,
        CancellationToken cancellationToken)
    {
        var mergedTools = new List<AITool>();
        var registeredToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var toolNames =  await _agentAppService.CollectNamedToolNamesAsync(agent.Id, agent.Tools);
        if (toolNames.Length > 0)
        {
            var functions =
                _toolRegistry.CreateAIFunctions(toolNames, ProjectDefaults.GetDefaultProjectIdentifier(projectId));
            if (functions.Count > 0)
            {
                AddUniqueTools(mergedTools, registeredToolNames, functions);
            }
        }

        var mcpTools = await ListToolsByAgentAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        if (mcpTools.Count > 0)
        {
            AddUniqueTools(mergedTools, registeredToolNames, mcpTools);
        }

        return mergedTools.Count > 0 ? mergedTools : null;
    }

    private static void AddUniqueTools(ICollection<AITool> destination, ISet<string> registeredToolNames,
        IEnumerable<AITool> tools)
    {
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || !registeredToolNames.Add(tool.Name))
            {
                continue;
            }

            destination.Add(tool);
        }
    }

    private async Task<IReadOnlyList<McpClientTool>> ListToolsByAgentAsync(Guid agentId,
        CancellationToken cancellationToken)
    {
        var servers = await _agentAppService.ListEnabledMcpToolServersByAgentAsync(agentId);
        var tools = new List<McpClientTool>();
        foreach (var server in servers)
        {
            try
            {
                var serverTools = await McpToolServerToolClient.ListToolsAsync(server, cancellationToken)
                    .ConfigureAwait(false);
                if (serverTools.Count > 0)
                {
                    tools.AddRange(serverTools);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list MCP tools from server {ServerId}", server.Id);
            }
        }

        return tools;
    }

    private async Task<AIContextProvider?> CreateSkillsProviderAsync(Guid agentId)
    {
        var skills = await _agentAppService.ListSkillsByAgentAsync(agentId);
        var skillPaths = skills
            .Select(GetSkillAbsolutePath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (skillPaths.Length == 0)
        {
            _logger.LogWarning(
                "Agent {AgentId} has skill relations configured but no extracted skill directories were found.",
                agentId);
            return null;
        }

        return new AgentSkillsProvider(skillPaths: skillPaths);
    }

    private string GetSkillAbsolutePath(Skill skill)
    {
        if (!string.IsNullOrWhiteSpace(skill.ContentPath))
        {
            var normalizedPath = skill.ContentPath.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(GetWebRootPath(), normalizedPath);
        }

        return Path.Combine(GetWebRootPath(), "skills", skill.Name);
    }

    private string GetWebRootPath()
    {
        if (!string.IsNullOrWhiteSpace(_webHostEnvironment.WebRootPath))
        {
            return _webHostEnvironment.WebRootPath;
        }

        return Path.Combine(_webHostEnvironment.ContentRootPath, "wwwroot");
    }

    #endregion
}
