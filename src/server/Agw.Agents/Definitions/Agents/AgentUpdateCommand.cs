using Agw.Agents.Definitions.Domain.Decisions;
using Agw.Shared.Tooling;

namespace Agw.Agents.Definitions.Agents;

public sealed class AgentUpdateCommand
{
    private readonly HashSet<AgentUpdateField> _specifiedFields;

    public AgentUpdateCommand(
        string? displayName,
        string? description,
        string? systemPrompt,
        Guid? modelProviderId,
        List<ToolValueObject>? tools,
        List<Guid>? mcpToolServerIds,
        List<Guid>? skillIds,
        List<Guid>? connectionIds,
        string? extra,
        Dictionary<string, string>? environmentVariables,
        bool? enableSummary,
        Guid? summaryModelProviderId,
        string? responseSchema,
        IEnumerable<AgentUpdateField> specifiedFields
    )
    {
        DisplayName = displayName;
        Description = description;
        SystemPrompt = systemPrompt;
        ModelProviderId = modelProviderId;
        Tools = tools;
        McpToolServerIds = mcpToolServerIds;
        SkillIds = skillIds;
        ConnectionIds = connectionIds;
        Extra = extra;
        EnvironmentVariables = environmentVariables;
        EnableSummary = enableSummary;
        SummaryModelProviderId = summaryModelProviderId;
        ResponseSchema = responseSchema;
        _specifiedFields = [.. specifiedFields];
    }

    public string? DisplayName { get; }
    public string? Description { get; }
    public string? SystemPrompt { get; }
    public Guid? ModelProviderId { get; }
    public List<ToolValueObject>? Tools { get; }
    public List<Guid>? McpToolServerIds { get; }
    public List<Guid>? SkillIds { get; }
    public List<Guid>? ConnectionIds { get; }
    public string? Extra { get; }
    public Dictionary<string, string>? EnvironmentVariables { get; }
    public bool? EnableSummary { get; }
    public Guid? SummaryModelProviderId { get; }
    public string? ResponseSchema { get; }

    public IReadOnlySet<AgentUpdateField> SpecifiedFields => _specifiedFields;

    public bool IsSpecified(AgentUpdateField field) => _specifiedFields.Contains(field);
}
