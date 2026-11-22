using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Providers.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Tooling;

namespace Agw.Agents.Definitions.Contracts;

public record AgentCreateRequest(
    string DisplayName,
    string Name,
    string Description,
    string SystemPrompt,
    Guid? ModelProviderId,
    List<ToolValueObject>? Tools = null,
    List<Guid>? McpToolServerIds = null,
    List<Guid>? SkillIds = null,
    List<Guid>? ConnectionIds = null,
    Dictionary<string, string>? EnvironmentVariables = null,
    bool EnableSummary = false,
    Guid? SummaryModelProviderId = null,
    AgentType? Type = null,
    EngineKind? ExternalAgentKind = null,
    string? Extra = null,
    string? ResponseSchema = null
)
{
    [JsonPropertyName("a2aEndpoint")]
    public A2AEndpointRequest? A2AEndpoint { get; init; }
}

public sealed record A2AOriginCredentialsRequest(string Origin, Dictionary<string, string?> Headers)
{
    internal static IReadOnlyList<A2AOriginCredentialsInput>? ToInputs(
        IReadOnlyList<A2AOriginCredentialsRequest>? credentials
    ) =>
        credentials
            ?.Select(group =>
                group is null
                    ? throw new AgwException(
                        ErrorCodes.InvalidParam,
                        "Each A2A credential group must specify an origin and headers."
                    )
                    : new A2AOriginCredentialsInput(group.Origin, group.Headers)
            )
            .ToArray();
}

public sealed record A2AEndpointRequest(
    string? CardUrl = null,
    IReadOnlyList<A2AOriginCredentialsRequest>? Credentials = null
)
{
    public A2AEndpointInput ToInput() => new(CardUrl, A2AOriginCredentialsRequest.ToInputs(Credentials));
}

public sealed record A2AOriginHeaderNamesDto(string Origin, IReadOnlyList<string> Names);

public sealed record A2AEndpointDto
{
    public required string CardUrl { get; init; }
    public required IReadOnlyList<A2AOriginHeaderNamesDto> CredentialHeaderNames { get; init; }
    public required string InterfaceUrl { get; init; }
    public required string ProtocolBinding { get; init; }
    public required bool Streaming { get; init; }
    public required AgentA2AEndpointExtra Extra { get; init; }

    public static A2AEndpointDto FromDomain(AgentA2AEndpoint endpoint) =>
        new()
        {
            CardUrl = endpoint.CardUrl,
            CredentialHeaderNames = A2AEndpointCredentialsJson
                .Deserialize(endpoint.Credentials)
                .HeadersByOrigin.Select(group => new A2AOriginHeaderNamesDto(group.Key, [.. group.Value.Keys]))
                .ToArray(),
            InterfaceUrl = endpoint.InterfaceUrl,
            ProtocolBinding = endpoint.ProtocolBinding,
            Streaming = endpoint.Streaming,
            Extra = endpoint.Extra,
        };
}

public sealed record A2ACardPreviewRequest(
    string CardUrl,
    IReadOnlyList<A2AOriginCredentialsRequest>? Credentials = null,
    Guid? AgentId = null
)
{
    internal A2AEndpointInput ToInput() => new(CardUrl, A2AOriginCredentialsRequest.ToInputs(Credentials));
}

public sealed record A2ACardPreviewResponse
{
    public required JsonElement Card { get; init; }
    public required string InterfaceUrl { get; init; }
    public required string ProtocolBinding { get; init; }
    public required bool Streaming { get; init; }
}

public sealed record A2ACardRefreshRequest(Guid AgentId);

public sealed record ExternalAgentOptionResponse(
    EngineKind Kind,
    string DisplayName,
    string DefaultExtra,
    IReadOnlyList<ProviderType> SupportedProviderTypes
);

public sealed record AgentEnabledUpdateRequest(Guid AgentId, bool Enable);

/// <summary>
/// <para>External Agent 支持部分更新，提供 displayName 或 description 时必须给出字段值。</para>
/// <para>A2A Agent 可更新 displayName、description 和 a2aEndpoint。</para>
/// <para>External agents support partial updates with non-null displayName and description when supplied.</para>
/// <para>A2A agents accept displayName, description, and a2aEndpoint.</para>
/// </summary>
public sealed class AgentUpdateRequest
{
    private readonly HashSet<AgentUpdateField> _specifiedFields = [];
    private string? _displayName;
    private string? _description;
    private string? _systemPrompt;
    private Guid? _modelProviderId;
    private List<ToolValueObject>? _tools;
    private List<Guid>? _mcpToolServerIds;
    private List<Guid>? _skillIds;
    private List<Guid>? _connectionIds;
    private string? _extra;
    private Dictionary<string, string>? _environmentVariables;
    private bool? _enableSummary;
    private Guid? _summaryModelProviderId;
    private string? _responseSchema;
    private A2AEndpointRequest? _a2aEndpoint;

    public string? DisplayName
    {
        get => _displayName;
        init
        {
            _displayName = value;
            _specifiedFields.Add(AgentUpdateField.DisplayName);
        }
    }

    public string? Description
    {
        get => _description;
        init
        {
            _description = value;
            _specifiedFields.Add(AgentUpdateField.Description);
        }
    }

    public string? SystemPrompt
    {
        get => _systemPrompt;
        init
        {
            _systemPrompt = value;
            _specifiedFields.Add(AgentUpdateField.SystemPrompt);
        }
    }

    public Guid? ModelProviderId
    {
        get => _modelProviderId;
        init
        {
            _modelProviderId = value;
            _specifiedFields.Add(AgentUpdateField.ModelProviderId);
        }
    }

    public List<ToolValueObject>? Tools
    {
        get => _tools;
        init
        {
            _tools = value;
            _specifiedFields.Add(AgentUpdateField.Tools);
        }
    }

    public List<Guid>? McpToolServerIds
    {
        get => _mcpToolServerIds;
        init
        {
            _mcpToolServerIds = value;
            _specifiedFields.Add(AgentUpdateField.McpToolServerIds);
        }
    }

    public List<Guid>? SkillIds
    {
        get => _skillIds;
        init
        {
            _skillIds = value;
            _specifiedFields.Add(AgentUpdateField.SkillIds);
        }
    }

    public List<Guid>? ConnectionIds
    {
        get => _connectionIds;
        init
        {
            _connectionIds = value;
            _specifiedFields.Add(AgentUpdateField.ConnectionIds);
        }
    }

    public string? Extra
    {
        get => _extra;
        init
        {
            _extra = value;
            _specifiedFields.Add(AgentUpdateField.Extra);
        }
    }

    public Dictionary<string, string>? EnvironmentVariables
    {
        get => _environmentVariables;
        init
        {
            _environmentVariables = value;
            _specifiedFields.Add(AgentUpdateField.EnvironmentVariables);
        }
    }

    public bool? EnableSummary
    {
        get => _enableSummary;
        init
        {
            _enableSummary = value;
            _specifiedFields.Add(AgentUpdateField.EnableSummary);
        }
    }

    public Guid? SummaryModelProviderId
    {
        get => _summaryModelProviderId;
        init
        {
            _summaryModelProviderId = value;
            _specifiedFields.Add(AgentUpdateField.SummaryModelProviderId);
        }
    }

    public string? ResponseSchema
    {
        get => _responseSchema;
        init
        {
            _responseSchema = value;
            _specifiedFields.Add(AgentUpdateField.ResponseSchema);
        }
    }

    [JsonPropertyName("a2aEndpoint")]
    public A2AEndpointRequest? A2AEndpoint
    {
        get => _a2aEndpoint;
        init
        {
            _a2aEndpoint = value;
            _specifiedFields.Add(AgentUpdateField.A2AEndpoint);
        }
    }

    public AgentUpdateCommand ToCommand() =>
        new(
            DisplayName,
            Description,
            SystemPrompt,
            ModelProviderId,
            Tools,
            McpToolServerIds,
            SkillIds,
            ConnectionIds,
            Extra,
            EnvironmentVariables,
            EnableSummary,
            SummaryModelProviderId,
            ResponseSchema,
            _specifiedFields
        )
        {
            A2AEndpoint = A2AEndpoint?.ToInput(),
        };
}

public sealed record AgentMcpToolServerRelationResponse(Guid AgentId, Guid McpToolServerId)
{
    public static AgentMcpToolServerRelationResponse FromDomain(AgentMcpServerRelation relation) =>
        new(relation.AgentId, relation.McpToolServerId);
}

public sealed record AgentSkillRelationResponse(Guid AgentId, Guid SkillId)
{
    public static AgentSkillRelationResponse FromDomain(AgentSkillRelation relation) =>
        new(relation.AgentId, relation.SkillId);
}

public sealed record AgentConnectionRelationResponse(Guid AgentId, Guid ConnectionId)
{
    public static AgentConnectionRelationResponse FromDomain(AgentConnectionRelation relation) =>
        new(relation.AgentId, relation.ConnectionId);
}

public sealed record AgentResponse(
    Guid Id,
    string DisplayName,
    string Name,
    string Description,
    bool Enable,
    string SystemPrompt,
    Guid? ModelProviderId,
    Guid? SummaryModelProviderId,
    bool EnableSummary,
    IReadOnlyList<ToolValueObject> Tools,
    AgentType Type,
    EngineKind ExternalAgentKind,
    string? Extra,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyList<AgentMcpToolServerRelationResponse> AgentMcpToolServers,
    IReadOnlyList<AgentSkillRelationResponse> AgentSkillRelations,
    IReadOnlyList<AgentConnectionRelationResponse> AgentConnectionRelations,
    DateTimeOffset CreateTime,
    string? CreateBy,
    DateTimeOffset? UpdateTime,
    string? UpdateBy,
    ResultFormat ResultFormat,
    string? ResponseSchema = null
)
{
    [JsonPropertyName("a2aEndpoint")]
    public A2AEndpointDto? A2AEndpoint { get; init; }

    public static AgentResponse FromDomain(Agent agent) =>
        new(
            agent.Id,
            agent.DisplayName,
            agent.Name,
            agent.Description,
            agent.Enable,
            agent.SystemPrompt,
            agent.ModelProviderId,
            agent.Type == AgentType.System ? agent.SummaryModelProviderId : null,
            agent.Type == AgentType.System && agent.EnableSummary,
            agent.Tools,
            agent.Type,
            agent.ExternalAgentKind,
            agent.Extra,
            agent.EnvironmentVariables,
            [.. agent.AgentMcpToolServers.Select(AgentMcpToolServerRelationResponse.FromDomain)],
            [.. agent.AgentSkillRelations.Select(AgentSkillRelationResponse.FromDomain)],
            [.. agent.AgentConnectionRelations.Select(AgentConnectionRelationResponse.FromDomain)],
            agent.CreateTime,
            agent.CreateBy,
            agent.UpdateTime,
            agent.UpdateBy,
            AgentResponseSchema.GetResultFormat(agent.ResponseSchema),
            agent.ResponseSchema
        )
        {
            A2AEndpoint = agent.A2AEndpoint is { } endpoint ? A2AEndpointDto.FromDomain(endpoint) : null,
        };
}

/// <summary>
/// Response for the agent option list (<c>GET /api/agents</c>) used by Chat, Job, Project, and
/// Agentflow selectors. Exposes the default Result format without including the response schema text.
/// </summary>
public sealed record AgentListResponse(
    Guid Id,
    string DisplayName,
    string Name,
    string Description,
    bool Enable,
    string SystemPrompt,
    Guid? ModelProviderId,
    Guid? SummaryModelProviderId,
    bool EnableSummary,
    IReadOnlyList<ToolValueObject> Tools,
    AgentType Type,
    EngineKind ExternalAgentKind,
    string? Extra,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyList<AgentMcpToolServerRelationResponse> AgentMcpToolServers,
    IReadOnlyList<AgentSkillRelationResponse> AgentSkillRelations,
    IReadOnlyList<AgentConnectionRelationResponse> AgentConnectionRelations,
    DateTimeOffset CreateTime,
    string? CreateBy,
    DateTimeOffset? UpdateTime,
    string? UpdateBy,
    ResultFormat ResultFormat
)
{
    public static AgentListResponse FromDomain(Agent agent) =>
        new(
            agent.Id,
            agent.DisplayName,
            agent.Name,
            agent.Description,
            agent.Enable,
            agent.SystemPrompt,
            agent.ModelProviderId,
            agent.Type == AgentType.System ? agent.SummaryModelProviderId : null,
            agent.Type == AgentType.System && agent.EnableSummary,
            agent.Tools,
            agent.Type,
            agent.ExternalAgentKind,
            agent.Extra,
            agent.EnvironmentVariables,
            [.. agent.AgentMcpToolServers.Select(AgentMcpToolServerRelationResponse.FromDomain)],
            [.. agent.AgentSkillRelations.Select(AgentSkillRelationResponse.FromDomain)],
            [.. agent.AgentConnectionRelations.Select(AgentConnectionRelationResponse.FromDomain)],
            agent.CreateTime,
            agent.CreateBy,
            agent.UpdateTime,
            agent.UpdateBy,
            AgentResponseSchema.GetResultFormat(agent.ResponseSchema)
        );
}
