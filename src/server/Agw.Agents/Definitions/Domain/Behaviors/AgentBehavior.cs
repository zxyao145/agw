using Agw.Agents.Definitions.Domain.Decisions;
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

    public void ApplyUpdate(AgentUpdateDecision decision)
    {
        var existing = _agent;
        ArgumentNullException.ThrowIfNull(decision);

        if (existing.Type == AgentType.External)
        {
            ApplyExternalUpdate(existing, decision);
        }
        else
        {
            ApplySystemUpdate(existing, decision);
        }

        NormalizeEnvironmentVariables(existing);
        EnsureAgentKindIsValid(existing);
        EnsureModelProviderIsPresentWhenRequired(existing);
        existing.Name = string.IsNullOrWhiteSpace(existing.Name) ? existing.Id.Normalize() : existing.Name;
    }

    // External Agent 只接受这里写入的字段；身份、名称、提示词与 Tools 属于外部运行时，摘要配置也不可开启。
    // An External agent accepts only the fields written here; identity, name, prompt and Tools belong to the external runtime, and summary settings cannot be enabled.
    private static void ApplyExternalUpdate(Agent agent, AgentUpdateDecision decision)
    {
        if (decision.SpecifiedFields.Contains(AgentUpdateField.DisplayName))
        {
            agent.DisplayName = decision.DisplayName!;
        }

        if (decision.SpecifiedFields.Contains(AgentUpdateField.Description))
        {
            agent.Description = decision.Description!;
        }

        if (decision.SpecifiedFields.Contains(AgentUpdateField.ModelProviderId))
        {
            agent.ModelProviderId = decision.ModelProviderId;
        }

        if (decision.SpecifiedFields.Contains(AgentUpdateField.Extra))
        {
            agent.Extra = decision.Extra;
        }

        if (decision.SpecifiedFields.Contains(AgentUpdateField.EnvironmentVariables))
        {
            agent.EnvironmentVariables = decision.EnvironmentVariables ?? new Dictionary<string, string>();
        }

        ApplyResponseSchemaUpdate(agent, decision);
        agent.EnableSummary = false;
        agent.SummaryModelProviderId = null;
    }

    // System Agent 的更新是整体替换，必填字段由 Application 校验；Extra 只对 External Agent 有意义，保持创建时的取值。
    // A System agent update replaces the whole configuration after Application validation; Extra only applies to External agents and keeps the value it got at creation.
    private static void ApplySystemUpdate(Agent agent, AgentUpdateDecision decision)
    {
        agent.DisplayName = decision.DisplayName!;
        agent.Description = decision.Description!;
        agent.SystemPrompt = decision.SystemPrompt!;
        agent.ModelProviderId = decision.ModelProviderId;
        agent.SummaryModelProviderId = decision.SummaryModelProviderId;
        agent.EnableSummary = decision.EnableSummary ?? false;
        agent.Tools = decision.Tools ?? [];
        agent.EnvironmentVariables = decision.EnvironmentVariables ?? new Dictionary<string, string>();
        ApplyResponseSchemaUpdate(agent, decision);
    }

    // 缺省字段保留当前值；已指定的取值由 Application 归一化后写入，空白归一化为 null 表示关闭结构化输出。
    // A missing field keeps the current value; a specified value arrives normalized, and blank normalizes to null to disable structured output.
    private static void ApplyResponseSchemaUpdate(Agent agent, AgentUpdateDecision decision)
    {
        if (decision.SpecifiedFields.Contains(AgentUpdateField.ResponseSchema))
        {
            agent.ResponseSchema = decision.ResponseSchema;
        }
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
