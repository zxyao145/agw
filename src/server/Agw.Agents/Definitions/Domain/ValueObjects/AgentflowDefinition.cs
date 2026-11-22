using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Definitions.Domain.ValueObjects;

public sealed record AgentflowDefinition
{
    public AgentflowDefinition(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyList<AgentflowBlockMember> members,
        IReadOnlyDictionary<string, AgentflowNodeOptions> nodeOptions,
        IReadOnlyDictionary<string, IReadOnlyList<AgentflowConditionRule>> edgeConditions
    )
    {
        Nodes = nodes;
        Edges = edges;
        Members = members;
        NodeOptions = nodeOptions;
        EdgeConditions = edgeConditions;
    }

    public AgentflowDefinition(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyList<AgentflowBlockMember> members
    )
        : this(
            nodes,
            edges,
            members,
            new Dictionary<string, AgentflowNodeOptions>(),
            new Dictionary<string, IReadOnlyList<AgentflowConditionRule>>()
        ) { }

    public IReadOnlyList<AgentflowNode> Nodes { get; init; }

    public IReadOnlyList<AgentflowEdge> Edges { get; init; }

    public IReadOnlyList<AgentflowBlockMember> Members { get; init; }

    public IReadOnlyDictionary<string, AgentflowNodeOptions> NodeOptions { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<AgentflowConditionRule>> EdgeConditions { get; init; }
}

public abstract record AgentflowNodeOptions;

public sealed record AgentflowConditionRule(string Field, string Value, string Path);

public sealed record AgentflowGateChoice(string ChoiceId, string Label);

public sealed record HumanGateOptions(
    string? HumanPrompt,
    IReadOnlyList<AgentflowGateChoice> Choices,
    bool AllowResponseText
) : AgentflowNodeOptions;

public sealed record CheckpointMarkerOptions(string? CheckpointName) : AgentflowNodeOptions;

public sealed record OutputOptions(bool EnableSummary) : AgentflowNodeOptions;

public sealed record HandoffBlockOptions : AgentflowNodeOptions
{
    public HandoffBlockOptions(
        string? handoffInstructions,
        bool? enableReturnToPrevious,
        bool? autonomous,
        int? autonomousTurnLimit,
        string? continuationPrompt
    )
    {
        HandoffInstructions = handoffInstructions;
        EnableReturnToPrevious = enableReturnToPrevious;
        Autonomous = autonomous;
        AutonomousTurnLimit = autonomousTurnLimit;
        ContinuationPrompt = continuationPrompt;
    }

    public string? HandoffInstructions { get; init; }

    public bool? EnableReturnToPrevious { get; init; }

    public bool? Autonomous { get; init; }

    public int? AutonomousTurnLimit { get; init; }

    public string? ContinuationPrompt { get; init; }
}

public sealed record GroupChatBlockOptions(int? MaxRounds) : AgentflowNodeOptions;

public sealed record MagenticBlockOptions : AgentflowNodeOptions
{
    public MagenticBlockOptions(int? maxRounds, int? maxStalls, int? maxResets, bool? requirePlanSignoff)
    {
        MaxRounds = maxRounds;
        MaxStalls = maxStalls;
        MaxResets = maxResets;
        RequirePlanSignoff = requirePlanSignoff;
    }

    public int? MaxRounds { get; init; }

    public int? MaxStalls { get; init; }

    public int? MaxResets { get; init; }

    public bool? RequirePlanSignoff { get; init; }
}
