using System.Security.Cryptography;
using System.Text.Json;
using Agw.Agents.Definitions.Agents;
using Agw.Projects.Contracts.Runtime;

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
            agent.AgentSkillRelations.Count != 0
            || agent.AgentMcpToolServers.Count != 0
            || agent.AgentConnectionRelations.Count != 0
            || project.SkillIds.Count != 0
            || project.McpServerIds.Count != 0
            || project.ConnectionIds.Count != 0
        )
            return null;

        var providers = new List<object>();
        foreach (
            var id in new[] { agent.ModelProviderId, agent.SummaryModelProviderId }.OfType<Guid>().Distinct().Order()
        )
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
}
