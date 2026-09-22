using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Domain.Rules;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;

namespace Agw.Agents.Definitions.Domain.Behaviors;

public sealed class AgentBehavior
{
    private readonly Agent _agent;

    public AgentBehavior(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _agent = agent;
    }

    public void PrepareForCreate()
    {
        var agent = _agent;

        EnsureAgentKindIsValid(agent);
        EnsureModelProviderIsPresentWhenRequired(agent);
        NormalizeEnvironmentVariables(agent);
        agent.Id = agent.Id == Guid.Empty ? Guid.CreateVersion7() : agent.Id;
        agent.Name = string.IsNullOrWhiteSpace(agent.Name) ? agent.Id.Normalize() : agent.Name;
    }

    public void ApplyUpdate(Action<Agent> updateAction)
    {
        var existing = _agent;
        ArgumentNullException.ThrowIfNull(updateAction);

        var originalExtra = existing.Extra;

        if (existing.Type == AgentType.External)
        {
            var originalId = existing.Id;
            var originalName = existing.Name;
            var originalSystemPrompt = existing.SystemPrompt;
            var originalTools = existing.Tools;
            var originalType = existing.Type;
            var originalExternalAgentKind = existing.ExternalAgentKind;

            updateAction(existing);

            existing.Id = originalId;
            existing.Name = originalName;
            existing.SystemPrompt = originalSystemPrompt;
            existing.Tools = originalTools;
            existing.Type = originalType;
            existing.ExternalAgentKind = originalExternalAgentKind;
            existing.EnableSummary = false;
            existing.SummaryModelProviderId = null;
        }
        else
        {
            updateAction(existing);
            existing.Extra = originalExtra;
        }

        NormalizeEnvironmentVariables(existing);
        EnsureAgentKindIsValid(existing);
        EnsureModelProviderIsPresentWhenRequired(existing);
        existing.Name = string.IsNullOrWhiteSpace(existing.Name) ? existing.Id.Normalize() : existing.Name;
    }

    private static void EnsureAgentKindIsValid(Agent agent)
    {
        if (agent.Type is not AgentType.System and not AgentType.External)
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"Agent type '{agent.Type}' is not supported.");
        }

        if (agent.Type == AgentType.System && agent.ExternalAgentKind != ExternalAgentKind.None)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "System agents cannot specify an external agent kind.");
        }

        if (
            agent.Type == AgentType.External
            && agent.ExternalAgentKind
                is not ExternalAgentKind.ClaudeCode
                    and not ExternalAgentKind.Codex
                    and not ExternalAgentKind.Pi
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "External agents require a supported external agent kind.");
        }
    }

    private static void EnsureModelProviderIsPresentWhenRequired(Agent agent)
    {
        if (agent.Type == AgentType.System && !agent.ModelProviderId.HasValue)
        {
            throw new AgwException(
                ErrorCodes.SystemAgentRequiresModelProvider,
                "System agents must have a ModelProviderId."
            );
        }

        if (agent.Type == AgentType.External && (agent.EnableSummary || agent.SummaryModelProviderId.HasValue))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "External agents do not support turn summary configuration."
            );
        }
    }

    private static void NormalizeEnvironmentVariables(Agent agent)
    {
        agent.EnvironmentVariables = EnvironmentVariableRules.Normalize(
            agent.EnvironmentVariables,
            ErrorCodes.InvalidAgentEnvironmentVariableName
        );
    }
}
