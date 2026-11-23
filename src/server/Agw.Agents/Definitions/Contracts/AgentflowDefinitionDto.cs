using System.Text.Json.Serialization;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Contracts;

public sealed record AgentflowDefinitionDto(
    IReadOnlyList<AgentflowNodeDto> Nodes,
    IReadOnlyList<AgentflowEdgeDto> Edges
);

public sealed record AgentflowPositionDto(double X, double Y);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AgentflowInputNodeDto), "input")]
[JsonDerivedType(typeof(AgentflowAgentNodeDto), "agent")]
[JsonDerivedType(typeof(AgentflowWorkflowAsAgentNodeDto), "workflowAsAgent")]
[JsonDerivedType(typeof(AgentflowPromptAdapterNodeDto), "promptAdapter")]
[JsonDerivedType(typeof(AgentflowClearMessagesNodeDto), "clearMessages")]
[JsonDerivedType(typeof(AgentflowHumanGateNodeDto), "humanGate")]
[JsonDerivedType(typeof(AgentflowCheckpointMarkerNodeDto), "checkpointMarker")]
[JsonDerivedType(typeof(AgentflowOutputNodeDto), "output")]
[JsonDerivedType(typeof(AgentflowConcurrentBlockNodeDto), "concurrentBlock")]
[JsonDerivedType(typeof(AgentflowHandoffBlockNodeDto), "handoffBlock")]
[JsonDerivedType(typeof(AgentflowGroupChatBlockNodeDto), "groupChatBlock")]
[JsonDerivedType(typeof(AgentflowMagenticBlockNodeDto), "magenticBlock")]
public abstract record AgentflowNodeDto
{
    protected AgentflowNodeDto(
        string nodeId,
        string? name,
        AgentflowPositionDto position,
        AgentflowRoutingStrategy routingStrategy
    )
    {
        NodeId = nodeId;
        Name = name;
        Position = position;
        RoutingStrategy = routingStrategy;
    }

    public string NodeId { get; init; }

    public string? Name { get; init; }

    public AgentflowPositionDto Position { get; init; }

    [JsonConverter(typeof(CamelCaseEnumConverter<AgentflowRoutingStrategy>))]
    public AgentflowRoutingStrategy RoutingStrategy { get; init; }
}

public sealed record AgentflowInputNodeDto : AgentflowNodeDto
{
    [JsonConstructor]
    public AgentflowInputNodeDto(
        string nodeId,
        string? name,
        AgentflowPositionDto position,
        AgentflowRoutingStrategy routingStrategy
    )
        : base(nodeId, name, position, routingStrategy) { }
}

public sealed record AgentflowAgentNodeDto : AgentflowNodeDto
{
    [JsonConstructor]
    public AgentflowAgentNodeDto(
        string nodeId,
        string? name,
        AgentflowPositionDto position,
        AgentflowRoutingStrategy routingStrategy,
        Guid? relateId,
        string? instructions
    )
        : base(nodeId, name, position, routingStrategy)
    {
        RelateId = relateId;
        Instructions = instructions;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RelateId { get; init; }

    public string? Instructions { get; init; }
}

public sealed record AgentflowWorkflowAsAgentNodeDto : AgentflowNodeDto
{
    [JsonConstructor]
    public AgentflowWorkflowAsAgentNodeDto(
        string nodeId,
        string? name,
        AgentflowPositionDto position,
        AgentflowRoutingStrategy routingStrategy,
        Guid? relateId,
        string? instructions
    )
        : base(nodeId, name, position, routingStrategy)
    {
        RelateId = relateId;
        Instructions = instructions;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RelateId { get; init; }

    public string? Instructions { get; init; }
}

public sealed record AgentflowPromptAdapterNodeDto : AgentflowNodeDto
{
    [JsonConstructor]
    public AgentflowPromptAdapterNodeDto(
        string nodeId,
        string? name,
        AgentflowPositionDto position,
        AgentflowRoutingStrategy routingStrategy,
        string? instructions
    )
        : base(nodeId, name, position, routingStrategy) => Instructions = instructions;

    public string? Instructions { get; init; }
}

public sealed record AgentflowClearMessagesNodeDto : AgentflowNodeDto
{
    [JsonConstructor]
    public AgentflowClearMessagesNodeDto(
        string nodeId,
        string? name,
        AgentflowPositionDto position,
        AgentflowRoutingStrategy routingStrategy
    )
        : base(nodeId, name, position, routingStrategy) { }
}

public sealed record AgentflowHumanGateNodeDto : AgentflowNodeDto
{
    [JsonConstructor]
    public AgentflowHumanGateNodeDto(
        string nodeId,
        string? name,
        AgentflowPositionDto position,
        AgentflowRoutingStrategy routingStrategy,
        string? humanPrompt,
        IReadOnlyList<AgentflowGateChoiceDto> choices,
        bool allowResponseText
    )
        : base(nodeId, name, position, routingStrategy)
    {
        HumanPrompt = humanPrompt;
        Choices = choices;
        AllowResponseText = allowResponseText;
    }

    public string? HumanPrompt { get; init; }

    public IReadOnlyList<AgentflowGateChoiceDto> Choices { get; init; }

    public bool AllowResponseText { get; init; }
}

public sealed record AgentflowCheckpointMarkerNodeDto : AgentflowNodeDto
{
    [JsonConstructor]
    public AgentflowCheckpointMarkerNodeDto(
        string nodeId,
        string? name,
        AgentflowPositionDto position,
        AgentflowRoutingStrategy routingStrategy,
        string? checkpointName
    )
        : base(nodeId, name, position, routingStrategy) => CheckpointName = checkpointName;

    public string? CheckpointName { get; init; }
}

public sealed record AgentflowOutputNodeDto : AgentflowNodeDto
{
    [JsonConstructor]
    public AgentflowOutputNodeDto(
        string nodeId,
        string? name,
        AgentflowPositionDto position,
        AgentflowRoutingStrategy routingStrategy,
        bool enableSummary,
        Guid? summaryModelProviderId,
        string? instructions
    )
        : base(nodeId, name, position, routingStrategy)
    {
        EnableSummary = enableSummary;
        SummaryModelProviderId = summaryModelProviderId;
        Instructions = instructions;
    }

    public bool EnableSummary { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SummaryModelProviderId { get; init; }

    public string? Instructions { get; init; }
}

public sealed record AgentflowConcurrentBlockNodeDto : AgentflowNodeDto
{
    [JsonConstructor]
    public AgentflowConcurrentBlockNodeDto(
        string nodeId,
        string? name,
        AgentflowPositionDto position,
        AgentflowRoutingStrategy routingStrategy,
        string? instructions,
        IReadOnlyList<AgentflowBlockMemberDto> members
    )
        : base(nodeId, name, position, routingStrategy)
    {
        Instructions = instructions;
        Members = members;
    }

    public string? Instructions { get; init; }

    public IReadOnlyList<AgentflowBlockMemberDto> Members { get; init; }
}

public sealed record AgentflowHandoffBlockNodeDto : AgentflowNodeDto
{
    [JsonConstructor]
    public AgentflowHandoffBlockNodeDto(
        string nodeId,
        string? name,
        AgentflowPositionDto position,
        AgentflowRoutingStrategy routingStrategy,
        string? instructions,
        IReadOnlyList<AgentflowBlockMemberDto> members,
        string? handoffInstructions,
        bool? enableReturnToPrevious,
        bool? autonomous,
        int? autonomousTurnLimit,
        string? continuationPrompt
    )
        : base(nodeId, name, position, routingStrategy)
    {
        Instructions = instructions;
        Members = members;
        HandoffInstructions = handoffInstructions;
        EnableReturnToPrevious = enableReturnToPrevious;
        Autonomous = autonomous;
        AutonomousTurnLimit = autonomousTurnLimit;
        ContinuationPrompt = continuationPrompt;
    }

    public string? Instructions { get; init; }

    public IReadOnlyList<AgentflowBlockMemberDto> Members { get; init; }

    public string? HandoffInstructions { get; init; }

    public bool? EnableReturnToPrevious { get; init; }

    public bool? Autonomous { get; init; }

    public int? AutonomousTurnLimit { get; init; }

    public string? ContinuationPrompt { get; init; }
}

public sealed record AgentflowGroupChatBlockNodeDto : AgentflowNodeDto
{
    [JsonConstructor]
    public AgentflowGroupChatBlockNodeDto(
        string nodeId,
        string? name,
        AgentflowPositionDto position,
        AgentflowRoutingStrategy routingStrategy,
        string? instructions,
        IReadOnlyList<AgentflowBlockMemberDto> members,
        int? maxRounds
    )
        : base(nodeId, name, position, routingStrategy)
    {
        Instructions = instructions;
        Members = members;
        MaxRounds = maxRounds;
    }

    public string? Instructions { get; init; }

    public IReadOnlyList<AgentflowBlockMemberDto> Members { get; init; }

    public int? MaxRounds { get; init; }
}

public sealed record AgentflowMagenticBlockNodeDto : AgentflowNodeDto
{
    [JsonConstructor]
    public AgentflowMagenticBlockNodeDto(
        string nodeId,
        string? name,
        AgentflowPositionDto position,
        AgentflowRoutingStrategy routingStrategy,
        string? instructions,
        IReadOnlyList<AgentflowBlockMemberDto> members,
        int? maxRounds,
        int? maxStalls,
        int? maxResets,
        bool? requirePlanSignoff
    )
        : base(nodeId, name, position, routingStrategy)
    {
        Instructions = instructions;
        Members = members;
        MaxRounds = maxRounds;
        MaxStalls = maxStalls;
        MaxResets = maxResets;
        RequirePlanSignoff = requirePlanSignoff;
    }

    public string? Instructions { get; init; }

    public IReadOnlyList<AgentflowBlockMemberDto> Members { get; init; }

    public int? MaxRounds { get; init; }

    public int? MaxStalls { get; init; }

    public int? MaxResets { get; init; }

    public bool? RequirePlanSignoff { get; init; }
}

public enum AgentflowBlockMemberKind
{
    Agent,
    WorkflowAsAgent,
}

public sealed record AgentflowBlockMemberDto
{
    [JsonConstructor]
    public AgentflowBlockMemberDto(
        string memberId,
        AgentflowBlockMemberKind kind,
        Guid? relateId,
        string name,
        string? instructions,
        bool isManager
    )
    {
        MemberId = memberId;
        Kind = kind;
        RelateId = relateId;
        Name = name;
        Instructions = instructions;
        IsManager = isManager;
    }

    public string MemberId { get; init; }

    [JsonRequired]
    [JsonConverter(typeof(CamelCaseEnumConverter<AgentflowBlockMemberKind>))]
    public AgentflowBlockMemberKind Kind { get; init; }

    public Guid? RelateId { get; init; }

    public string Name { get; init; }

    public string? Instructions { get; init; }

    public bool IsManager { get; init; }
}

public sealed record AgentflowGateChoiceDto(string ChoiceId, string Label);

[JsonConverter(typeof(CamelCaseEnumConverter<AgentflowConditionField>))]
public enum AgentflowConditionField
{
    Contains,
    NotContains,
    Equals,
    Author,
    Role,
    MinMessages,
    Result,
}

public sealed record AgentflowConditionRuleDto(
    [property: JsonRequired] AgentflowConditionField Field,
    string Value,
    string Path
);

public sealed record AgentflowEdgeDto
{
    [JsonConstructor]
    public AgentflowEdgeDto(
        string edgeId,
        string sourceNodeId,
        string targetNodeId,
        bool isFanInBarrier,
        bool isSwitchDefault,
        int switchCaseOrder,
        string? choiceId,
        string? label,
        IReadOnlyList<AgentflowConditionRuleDto> conditions
    )
    {
        EdgeId = edgeId;
        SourceNodeId = sourceNodeId;
        TargetNodeId = targetNodeId;
        IsFanInBarrier = isFanInBarrier;
        IsSwitchDefault = isSwitchDefault;
        SwitchCaseOrder = switchCaseOrder;
        ChoiceId = choiceId;
        Label = label;
        Conditions = conditions;
    }

    public string EdgeId { get; init; }

    public string SourceNodeId { get; init; }

    public string TargetNodeId { get; init; }

    public bool IsFanInBarrier { get; init; }

    public bool IsSwitchDefault { get; init; }

    public int SwitchCaseOrder { get; init; }

    public string? ChoiceId { get; init; }

    public string? Label { get; init; }

    public IReadOnlyList<AgentflowConditionRuleDto> Conditions { get; init; }
}
