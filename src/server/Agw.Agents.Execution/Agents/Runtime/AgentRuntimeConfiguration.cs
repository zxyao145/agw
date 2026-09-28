using System.Security.Cryptography;
using System.Text.Json;
using Agw.Agents.Definitions.Agents;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Tooling;

namespace Agw.Agents.Execution.Agents.Runtime;

/// <summary>Checks the materialized configuration through owner-module read facades.</summary>
public sealed class AgentRuntimeConfiguration
{
    private readonly AgentAppService _agents;
    private readonly IProjectRuntimeFacade _projects;

    public AgentRuntimeConfiguration(AgentAppService agents, IProjectRuntimeFacade projects)
    {
        _agents = agents;
        _projects = projects;
    }

    public async Task<string?> ReadAsync(Guid agentId, Guid projectId, CancellationToken cancellationToken)
    {
        var agent = await _agents.GetAgentForCurrentUserAsync(agentId);
        var project = await _projects.GetForCurrentUserAsync(projectId, cancellationToken);
        if (agent == null || project == null)
            return null;
        // These capabilities can change independently (remote catalog, credentials, files). Until their
        // owners expose a complete revision, rebuild between turns rather than reuse a stale capability set.
        if (
            HasIndependentCapabilities(agent)
            || project.SkillIds.Count != 0
            || project.McpServerIds.Count != 0
            || project.ConnectionIds.Count != 0
        )
            return null;

        // Background Agent 随父 Runtime 一起创建，所以它们的定义也属于父 Runtime 的版本。
        // Background Agents are created together with the parent Runtime, so their definitions belong to the parent's version as well.
        var backgroundAgents = new List<Agent>();
        foreach (var id in GetBackgroundAgentIds(agent))
        {
            var backgroundAgent = await _agents.GetAgentForCurrentUserAsync(id);
            if (backgroundAgent == null)
                continue;
            if (HasIndependentCapabilities(backgroundAgent))
                return null;
            backgroundAgents.Add(backgroundAgent);
        }

        var providers = new List<object>();
        var providerIds = backgroundAgents
            .Prepend(agent)
            .SelectMany(static definition => new[] { definition.ModelProviderId, definition.SummaryModelProviderId })
            .OfType<Guid>()
            .Distinct()
            .Order();
        foreach (var id in providerIds)
        {
            var config = await _agents.GetModelRuntimeConfigurationAsync(id);
            if (config == null)
                return null;
            providers.Add(
                new
                {
                    Id = id,
                    config.Model,
                    ProviderId = config.Provider.Id,
                    config.Provider.Name,
                    config.Provider.ProviderType,
                    config.Provider.Endpoint,
                    Auth = config.Provider.AuthConfigs.Select(auth => new { auth.Enable, auth.ApiKey }).ToArray(),
                }
            );
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                Agent = CreateSnapshot(agent),
                BackgroundAgents = backgroundAgents.Select(CreateSnapshot).ToArray(),
                Project = new
                {
                    project.Id,
                    project.Name,
                    project.Workspace,
                    project.ExtraSetting,
                    project.Tools,
                    Environment = project.EnvironmentVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal),
                },
                Providers = providers,
            }
        );
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool HasIndependentCapabilities(Agent agent) =>
        agent.AgentSkillRelations.Count != 0
        || agent.AgentMcpToolServers.Count != 0
        || agent.AgentConnectionRelations.Count != 0;

    private static IEnumerable<Guid> GetBackgroundAgentIds(Agent agent) =>
        agent
            .Tools.OfType<ToolBlockValue>()
            .Select(static value => value.Definition)
            .OfType<BackgroundAgentsToolBlockDefinition>()
            .SelectMany(static definition => definition.Options.AllowedAgentIds)
            .Where(id => id != Guid.Empty && id != agent.Id)
            .Distinct()
            .Order();

    private static object CreateSnapshot(Agent agent) =>
        new
        {
            agent.Id,
            agent.Name,
            agent.DisplayName,
            agent.Description,
            agent.SystemPrompt,
            agent.Type,
            agent.ExternalAgentKind,
            agent.Extra,
            agent.ResponseSchema,
            agent.Enable,
            agent.EnableSummary,
            agent.ModelProviderId,
            agent.SummaryModelProviderId,
            agent.UpdateTime,
            agent.CreateTime,
            agent.Tools,
            Environment = agent.EnvironmentVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal),
        };
}
