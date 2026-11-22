using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Domain.Rules;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Agw.Shared.Tooling;

namespace Agw.Agents.Definitions.Domain.Behaviors;

public sealed class AgentBehavior
{
    private static readonly (AgentUpdateField Field, string JsonName)[] ExternalUnsupportedFields =
    [
        (AgentUpdateField.SystemPrompt, "systemPrompt"),
        (AgentUpdateField.Tools, "tools"),
        (AgentUpdateField.SkillIds, "skillIds"),
        (AgentUpdateField.McpToolServerIds, "mcpToolServerIds"),
        (AgentUpdateField.ConnectionIds, "connectionIds"),
        (AgentUpdateField.EnableSummary, "enableSummary"),
        (AgentUpdateField.SummaryModelProviderId, "summaryModelProviderId"),
    ];

    private readonly Agent _agent;

    private bool UsesA2AEndpoint => ExternalAgentRules.UsesA2AEndpoint(_agent);

    public AgentBehavior(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _agent = agent;
    }

    public void PrepareForCreate(A2AEndpointInput? a2aEndpoint = null)
    {
        var agent = _agent;

        EnsureToolsAreValid(agent.Tools);
        EnsureResponseSchemaSupported(agent.ExternalAgentKind, agent.ResponseSchema);
        EnsureAgentKindIsValid(agent);
        EnsureA2AEndpointInput(a2aEndpoint);
        if (UsesA2AEndpoint)
        {
            EnsureA2ASettings(agent.ModelProviderId, agent.Extra, agent.EnvironmentVariables, agent.ResponseSchema);
            agent.Extra = A2AEndpointRules.EmptyExtra;
        }

        EnsureModelProviderIsPresentWhenRequired(agent);
        NormalizeEnvironmentVariables(agent);
        agent.Id = agent.Id == Guid.Empty ? Guid.CreateVersion7() : agent.Id;
        agent.Name = string.IsNullOrWhiteSpace(agent.Name) ? agent.Id.Normalize() : agent.Name;
    }

    /// <summary>
    /// <para>校验一次更新是否适用于当前 Agent 类型：External Agent 只能更新外部运行时之外的字段，System Agent 的更新是带齐必填字段的整体替换。</para>
    /// <para>Checks that an update fits the agent type: an External agent updates only fields outside its external runtime, and a System agent update is a full replacement carrying every required field.</para>
    /// </summary>
    public void EnsureUpdateAllowed(AgentUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.SpecifiedFields.Contains(AgentUpdateField.A2AEndpoint))
        {
            EnsureA2AEndpointInput(update.A2AEndpoint);
        }

        if (UsesA2AEndpoint)
        {
            EnsureA2ASettings(update.ModelProviderId, update.Extra, update.EnvironmentVariables, update.ResponseSchema);
        }

        if (_agent.Type == AgentType.External)
        {
            EnsureExternalUpdateAllowed(update);
        }
        else
        {
            EnsureSystemUpdateAllowed(update);
            EnsureToolsAreValid(update.Tools);
        }

        if (update.SpecifiedFields.Contains(AgentUpdateField.ResponseSchema))
        {
            EnsureResponseSchemaSupported(_agent.ExternalAgentKind, update.ResponseSchema);
        }
    }

    /// <summary>
    /// <para>返回更新后 Agent 将使用的模型供应商：System Agent 的更新总是带有该字段，External Agent 省略时保留当前值。</para>
    /// <para>Returns the model provider the agent uses after the update: a System agent update always carries it, and an External agent keeps the current one when it is omitted.</para>
    /// </summary>
    public Guid? ResolveModelProviderId(AgentUpdate update) =>
        update.SpecifiedFields.Contains(AgentUpdateField.ModelProviderId)
            ? update.ModelProviderId
            : _agent.ModelProviderId;

    public void ApplyUpdate(AgentUpdate update)
    {
        var existing = _agent;
        EnsureUpdateAllowed(update);

        if (existing.Type == AgentType.External)
        {
            ApplyExternalUpdate(existing, update);
            if (UsesA2AEndpoint)
            {
                existing.Extra = A2AEndpointRules.EmptyExtra;
            }
        }
        else
        {
            ApplySystemUpdate(existing, update);
        }

        NormalizeEnvironmentVariables(existing);
        EnsureAgentKindIsValid(existing);
        EnsureModelProviderIsPresentWhenRequired(existing);
        existing.Name = string.IsNullOrWhiteSpace(existing.Name) ? existing.Id.Normalize() : existing.Name;
    }

    /// <summary>
    /// <para>System Agent 的更新整体替换它的 MCP Server、Skill 与 Connection 绑定；External Agent 的更新不改动绑定。</para>
    /// <para>A System agent update replaces its MCP server, skill and connection bindings; an External agent update leaves bindings unchanged.</para>
    /// </summary>
    public bool UpdatesResourceBindings() => _agent.Type == AgentType.System;

    public A2AEndpointInput? ResolveA2AEndpointInput(A2AEndpointInput? input) =>
        input is { CardUrl: null } ? input with { CardUrl = _agent.A2AEndpoint?.CardUrl } : input;

    public void EnsureA2AEndpointInput(A2AEndpointInput? input)
    {
        if (UsesA2AEndpoint != (input is not null))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                UsesA2AEndpoint ? "A2A agents require an endpoint." : "Only A2A agents can specify an A2A endpoint."
            );
        }
    }

    public void SetA2AEndpoint(A2AEndpointSnapshot snapshot)
    {
        var changed = _agent.A2AEndpoint is not { } current || InterfaceChanged(current, snapshot.Interface);
        var endpoint = _agent.A2AEndpoint ??= new AgentA2AEndpoint { AgentId = _agent.Id, Extra = snapshot.Extra };
        endpoint.CardUrl = snapshot.CardUrl;
        endpoint.Credentials = snapshot.Credentials;
        endpoint.InterfaceUrl = snapshot.Interface.Url;
        endpoint.ProtocolBinding = snapshot.Interface.ProtocolBinding;
        if (changed)
        {
            endpoint.InterfaceRevision = Guid.CreateVersion7();
        }
        endpoint.Streaming = snapshot.Streaming;
        endpoint.Extra = snapshot.Extra;
    }

    private static bool InterfaceChanged(AgentA2AEndpoint endpoint, A2ASelectedInterface selected)
    {
        var previous = new Uri(endpoint.InterfaceUrl);
        var current = new Uri(selected.Url);
        return endpoint.ProtocolBinding != selected.ProtocolBinding
            || A2AEndpointRules.NormalizeOrigin(previous) != A2AEndpointRules.NormalizeOrigin(current)
            || previous.PathAndQuery != current.PathAndQuery;
    }

    private static void EnsureA2ASettings(
        Guid? modelProviderId,
        string? extra,
        IReadOnlyDictionary<string, string>? environmentVariables,
        string? responseSchema
    )
    {
        var hasExtraSettings = !string.IsNullOrWhiteSpace(extra) && extra != A2AEndpointRules.EmptyExtra;
        var hasUnsupportedSettings =
            modelProviderId is not null
            || hasExtraSettings
            || environmentVariables is { Count: > 0 }
            || responseSchema is not null;
        if (hasUnsupportedSettings)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "A2A agents do not support modelProviderId, extra settings, environmentVariables, or responseSchema."
            );
        }
    }

    public void RetainVisibleRelations(IReadOnlySet<Guid> visibleSkillIds, IReadOnlySet<Guid> visibleConnectionIds)
    {
        _agent.AgentSkillRelations = _agent
            .AgentSkillRelations.Where(relation => visibleSkillIds.Contains(relation.SkillId))
            .ToList();
        _agent.AgentConnectionRelations = _agent
            .AgentConnectionRelations.Where(relation => visibleConnectionIds.Contains(relation.ConnectionId))
            .ToList();
    }

    private static void EnsureExternalUpdateAllowed(AgentUpdate update)
    {
        var unsupportedFields = ExternalUnsupportedFields
            .Where(field => update.SpecifiedFields.Contains(field.Field))
            .Select(field => field.JsonName)
            .ToArray();
        if (unsupportedFields.Length > 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"External agents cannot update fields: {string.Join(", ", unsupportedFields)}."
            );
        }

        if (update.SpecifiedFields.Contains(AgentUpdateField.DisplayName) && update.DisplayName == null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "displayName cannot be null.");
        }

        if (update.SpecifiedFields.Contains(AgentUpdateField.Description) && update.Description == null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "description cannot be null.");
        }
    }

    private static void EnsureSystemUpdateAllowed(AgentUpdate update)
    {
        var missingFields = new List<string>();
        if (!update.SpecifiedFields.Contains(AgentUpdateField.DisplayName) || update.DisplayName == null)
        {
            missingFields.Add("displayName");
        }

        if (!update.SpecifiedFields.Contains(AgentUpdateField.Description) || update.Description == null)
        {
            missingFields.Add("description");
        }

        if (!update.SpecifiedFields.Contains(AgentUpdateField.SystemPrompt) || update.SystemPrompt == null)
        {
            missingFields.Add("systemPrompt");
        }

        if (!update.SpecifiedFields.Contains(AgentUpdateField.ModelProviderId))
        {
            missingFields.Add("modelProviderId");
        }

        if (update.SpecifiedFields.Contains(AgentUpdateField.EnableSummary) && update.EnableSummary == null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "enableSummary cannot be null.");
        }

        if (missingFields.Count > 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"System agent update requires fields: {string.Join(", ", missingFields)}."
            );
        }
    }

    // External Agent 只接受这里写入的字段；身份、名称、提示词与 Tools 属于外部运行时，摘要配置也不可开启。
    // An External agent accepts only the fields written here; identity, name, prompt and Tools belong to the external runtime, and summary settings cannot be enabled.
    private static void ApplyExternalUpdate(Agent agent, AgentUpdate update)
    {
        if (update.SpecifiedFields.Contains(AgentUpdateField.DisplayName))
        {
            agent.DisplayName = update.DisplayName!;
        }

        if (update.SpecifiedFields.Contains(AgentUpdateField.Description))
        {
            agent.Description = update.Description!;
        }

        if (update.SpecifiedFields.Contains(AgentUpdateField.ModelProviderId))
        {
            agent.ModelProviderId = update.ModelProviderId;
        }

        if (update.SpecifiedFields.Contains(AgentUpdateField.Extra))
        {
            agent.Extra = update.Extra;
        }

        if (update.SpecifiedFields.Contains(AgentUpdateField.EnvironmentVariables))
        {
            agent.EnvironmentVariables = update.EnvironmentVariables ?? new Dictionary<string, string>();
        }

        ApplyResponseSchemaUpdate(agent, update);
        agent.EnableSummary = false;
        agent.SummaryModelProviderId = null;
    }

    // System Agent 的更新是整体替换，必填字段已经过校验；Extra 只对 External Agent 有意义，保持创建时的取值。
    // A System agent update replaces the whole configuration after its required fields pass validation; Extra only applies to External agents and keeps the value it got at creation.
    private static void ApplySystemUpdate(Agent agent, AgentUpdate update)
    {
        agent.DisplayName = update.DisplayName!;
        agent.Description = update.Description!;
        agent.SystemPrompt = update.SystemPrompt!;
        agent.ModelProviderId = update.ModelProviderId;
        agent.SummaryModelProviderId = update.SummaryModelProviderId;
        agent.EnableSummary = update.EnableSummary ?? false;
        agent.Tools = update.Tools ?? [];
        agent.EnvironmentVariables = update.EnvironmentVariables ?? new Dictionary<string, string>();
        ApplyResponseSchemaUpdate(agent, update);
    }

    // 缺省字段保留当前值；已指定的取值由 Application 归一化后写入，空白归一化为 null 表示关闭结构化输出。
    // A missing field keeps the current value; a specified value arrives normalized, and blank normalizes to null to disable structured output.
    private static void ApplyResponseSchemaUpdate(Agent agent, AgentUpdate update)
    {
        if (update.SpecifiedFields.Contains(AgentUpdateField.ResponseSchema))
        {
            agent.ResponseSchema = update.ResponseSchema;
        }
    }

    private static void EnsureToolsAreValid(IReadOnlyList<ToolValueObject>? tools)
    {
        var toolsError = ToolValueObjectValidation.GetError(tools);
        if (toolsError != null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, toolsError);
        }
    }

    // Pi 运行无法强制结构化输出，因此直接拒绝带 responseSchema 的配置。
    // Pi runs cannot enforce a response schema, so a configuration that sets one is rejected.
    private static void EnsureResponseSchemaSupported(EngineKind kind, string? responseSchema)
    {
        if (kind == EngineKind.Pi && responseSchema != null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Pi agents do not support responseSchema.");
        }
    }

    private static void EnsureAgentKindIsValid(Agent agent)
    {
        if (agent.Type is not AgentType.System and not AgentType.External)
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"Agent type '{agent.Type}' is not supported.");
        }

        if (agent.Type == AgentType.System && agent.ExternalAgentKind != EngineKind.Maf)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "System agents cannot specify an external agent kind.");
        }

        if (agent.Type == AgentType.External && !ExternalAgentRules.SupportedKinds.Contains(agent.ExternalAgentKind))
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
