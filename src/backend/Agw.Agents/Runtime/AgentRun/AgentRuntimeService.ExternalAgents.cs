using System.Diagnostics.CodeAnalysis;

using Agw.Agents.Runtime.AgentRun.Dtos;
using Agw.Agents.ExternalAgents;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;

using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

using OpenAI.CodexSdk.MAF;

namespace Agw.Agents.Runtime.AgentRun;

public partial class AgentRuntimeService
{
    private bool TryCreateExternalAgent(
        CreateAiAgentRequest request,
        Project project,
        [NotNullWhen(true)] out AIAgent? aiAgent)
    {
        aiAgent = null;
        if (request.Agent.Type != AgentType.External)
        {
            return false;
        }

        aiAgent = request.Agent.Name switch
        {
            AgentNames.ClaudeCode => CreateClaudeCodeAgent(
                project,
                request.ProviderSessionId,
                request.Resume,
                request.EnvironmentVariables),
            AgentNames.Codex => CreateCodexAgent(
                project,
                request.ProviderSessionId,
                request.Resume,
                request.EnvironmentVariables,
                request.OnExternalSessionStartedAsync),
            _ => null
        };

        if (aiAgent != null)
        {
            aiAgent = aiAgent.AsBuilder()
                .Use(
                    runFunc: _loggingMiddleware.LogRunMiddleware,
                    runStreamingFunc: _loggingMiddleware.LogStreamingMiddleware)
                .Build();
        }
        
        return aiAgent != null;
    }

    private AIAgent? CreateClaudeCodeAgent(
        Project project,
        Guid? contextId,
        bool resume,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        string? extra = project.ExtraSetting;
        if (string.IsNullOrWhiteSpace(extra) || IsEmptyJsonObject(extra))
        {
            extra = JsonUtil.Serialize(new ClaudeCodeAIAgentOptions
            {
                PermissionMode = PermissionMode.bypassPermissions,
            });
        }

        var options = JsonUtil.Deserialize<ClaudeCodeAIAgentOptions>(extra);
        if (options == null)
        {
            _logger.LogError("agent.Extra Deserialize to options error");
            return null;
        }

        options = options with
        {
            WorkingDirectory = PathUtil.ExpandTilde(project.Workspace),
            ChatHistoryProvider = _chatHistoryProvider
        };

        if (contextId != null)
        {
            options = resume
                ? options with { Resume = contextId.Value.Normalize(), SessionId = null }
                : options with { Resume = null, SessionId = contextId };
        }

        options = AgentRuntimeServiceUtil.ApplyEnvironmentVariables(options, environmentVariables);
        return new ClaudeCodeAIAgent(options, _logger);
    }

    private AIAgent? CreateCodexAgent(
        Project project,
        Guid? threadId,
        bool resume,
        IReadOnlyDictionary<string, string>? environmentVariables,
        Func<string, CancellationToken, ValueTask>? onThreadStartedAsync)
    {
        string? extra = project.ExtraSetting;
        if (string.IsNullOrWhiteSpace(extra) || IsEmptyJsonObject(extra))
        {
            extra = JsonUtil.Serialize(new CodexAIAgentOptions());
        }

        var options = AgentRuntimeServiceUtil.BuildCodexAIAgentOptions(
            extra,
            PathUtil.ExpandTilde(project.Workspace),
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

    private static bool IsCodexExternalAgent(Agent agent) =>
        agent.Type == AgentType.External
        && string.Equals(agent.Name, AgentNames.Codex, StringComparison.OrdinalIgnoreCase);

    private static bool IsEmptyJsonObject(string value)
    {
        var hasOpen = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (!hasOpen)
            {
                if (c != '{') return false;
                hasOpen = true;
                continue;
            }

            if (c != '}') return false;
            return true;
        }

        return false;
    }
}
