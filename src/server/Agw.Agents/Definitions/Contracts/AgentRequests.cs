using Agw.Agents.Definitions.Agents;
using Agw.Shared.Data.Entities.Agents;
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
    Guid? SummaryModelProviderId = null
);

public sealed record AgentEnabledUpdateRequest(Guid AgentId, bool Enable);

/// <summary>
/// Updates an agent. External agents support partial updates for displayName, description,
/// modelProviderId, extra, and environmentVariables only. When supplied for an External agent,
/// displayName and description cannot be null.
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
            _specifiedFields
        );
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
    string? Extra,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyList<AgentMcpToolServerRelationResponse> AgentMcpToolServers,
    IReadOnlyList<AgentSkillRelationResponse> AgentSkillRelations,
    IReadOnlyList<AgentConnectionRelationResponse> AgentConnectionRelations,
    DateTimeOffset CreateTime,
    string? CreateBy,
    DateTimeOffset? UpdateTime,
    string? UpdateBy
)
{
    public static AgentResponse FromDomain(Agent agent) =>
        new(
            agent.Id,
            agent.DisplayName,
            agent.Name,
            agent.Description,
            agent.Enable,
            agent.SystemPrompt,
            agent.ModelProviderId,
            agent.SummaryModelProviderId,
            agent.EnableSummary,
            agent.Tools,
            agent.Type,
            agent.Extra,
            agent.EnvironmentVariables,
            [.. agent.AgentMcpToolServers.Select(AgentMcpToolServerRelationResponse.FromDomain)],
            [.. agent.AgentSkillRelations.Select(AgentSkillRelationResponse.FromDomain)],
            [.. agent.AgentConnectionRelations.Select(AgentConnectionRelationResponse.FromDomain)],
            agent.CreateTime,
            agent.CreateBy,
            agent.UpdateTime,
            agent.UpdateBy
        );
}
