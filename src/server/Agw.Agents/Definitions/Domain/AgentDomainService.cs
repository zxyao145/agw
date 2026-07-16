using System.Text.Json;

using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;

namespace Agw.Agents.Definitions.Domain;

public class AgentDomainService
{
    private readonly TimeProvider _timeProvider;

    public AgentDomainService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public void PrepareForCreate(Agent agent, string user)
    {
        ArgumentNullException.ThrowIfNull(agent);

        EnsureModelProviderIsPresentWhenRequired(agent);
        NormalizeEnvironmentVariables(agent);
        agent.Id = agent.Id == Guid.Empty ? Guid.CreateVersion7() : agent.Id;
        agent.Name = string.IsNullOrWhiteSpace(agent.Name) ? agent.Id.Normalize() : agent.Name;
        agent.CreateBy = user;
        agent.CreateTime = _timeProvider.GetUtcNow();
    }

    public void ApplyUpdate(Agent existing, Action<Agent> updateAction, string user)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(updateAction);

        var originalExtra = existing.Extra;

        if (existing.Type == AgentType.External)
        {
            var originalId = existing.Id;
            var originalName = existing.Name;
            var originalSystemPrompt = existing.SystemPrompt;
            var originalTools = existing.Tools;
            var originalType = existing.Type;

            updateAction(existing);

            existing.Id = originalId;
            existing.Name = originalName;
            existing.SystemPrompt = originalSystemPrompt;
            existing.Tools = originalTools;
            existing.Type = originalType;
            existing.Extra = NormalizeExtraSettings(existing.Extra);
        }
        else
        {
            updateAction(existing);
            existing.Extra = originalExtra;
        }

        NormalizeEnvironmentVariables(existing);
        EnsureModelProviderIsPresentWhenRequired(existing);
        existing.Name = string.IsNullOrWhiteSpace(existing.Name) ? existing.Id.Normalize() : existing.Name;
        existing.UpdateBy = user;
        existing.UpdateTime = _timeProvider.GetUtcNow();
    }

    public IReadOnlyList<Guid> NormalizeMcpToolServerIds(IEnumerable<Guid>? mcpToolServerIds)
    {
        return (mcpToolServerIds ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
    }

    private static void EnsureModelProviderIsPresentWhenRequired(Agent agent)
    {
        if (agent.Type == AgentType.System && !agent.ModelProviderId.HasValue)
        {
            throw new AgwException(ErrorCodes.SystemAgentRequiresModelProvider, "System agents must have a ModelProviderId.");
        }

        if (agent.Type == AgentType.External &&
            agent.EnableSummary &&
            !agent.SummaryModelProviderId.HasValue)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "External agent Summary requires a SummaryModelProviderId.");
        }
    }

    private static string? NormalizeExtraSettings(string? extra)
    {
        var normalized = extra?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(normalized);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return normalized;
            }
        }
        catch (JsonException)
        {
        }

        throw new AgwException(ErrorCodes.InvalidAgentExtraSettings);
    }

    private static void NormalizeEnvironmentVariables(Agent agent)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in agent.EnvironmentVariables ?? [])
        {
            var normalizedName = name.Trim();
            if (string.IsNullOrEmpty(normalizedName)
                || normalizedName.Contains('=')
                || normalizedName.Contains('\0')
                || !normalized.TryAdd(normalizedName, value ?? string.Empty))
            {
                throw new AgwException(ErrorCodes.InvalidAgentEnvironmentVariableName);
            }
        }

        agent.EnvironmentVariables = normalized;
    }
}
