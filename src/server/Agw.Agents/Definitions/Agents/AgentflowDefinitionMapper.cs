using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Definitions.Agents;

public static class AgentflowDefinitionMapper
{
    public static AgentflowDefinition ToDefinition(AgentflowDefinitionDto dto)
    {
        var nodes = dto.Nodes.Select(MapNode).ToArray();
        var nodeOptions = ReadNodeOptionsMap(nodes);
        var members = MapMembers(dto.Nodes);
        var edgeConditions = MapEdgeConditions(dto.Edges);
        var edges = dto.Edges.Select(edge => MapEdge(edge, edgeConditions[edge.EdgeId])).ToArray();

        return new AgentflowDefinition(nodes, edges, members, nodeOptions, edgeConditions);
    }

    private static AgentflowBlockMember[] MapMembers(IReadOnlyList<AgentflowNodeDto> nodes) =>
        nodes
            .SelectMany(node =>
                EnumerateMembers(node)
                    .Select(
                        (member, index) =>
                            new AgentflowBlockMember
                            {
                                AgentflowId = Guid.Empty,
                                AgentflowRevision = 0,
                                NodeId = node.NodeId,
                                MemberId = member.MemberId,
                                Kind = ToNodeKind(member.Kind),
                                RelateId = member.RelateId ?? Guid.Empty,
                                Name = member.Name,
                                Instructions = member.Instructions,
                                SortOrder = index,
                                IsManager = member.IsManager,
                            }
                    )
            )
            .ToArray();

    private static IReadOnlyDictionary<string, IReadOnlyList<AgentflowConditionRule>> MapEdgeConditions(
        IReadOnlyList<AgentflowEdgeDto> edges
    ) =>
        edges
            .GroupBy(edge => edge.EdgeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                    (IReadOnlyList<AgentflowConditionRule>)
                        group
                            .First()
                            .Conditions.Select(condition => new AgentflowConditionRule(
                                ToDomainConditionField(condition.Field),
                                condition.Value,
                                condition.Path
                            ))
                            .ToArray(),
                StringComparer.Ordinal
            );

    private static AgentflowEdge MapEdge(AgentflowEdgeDto edge, IReadOnlyList<AgentflowConditionRule> conditions) =>
        new()
        {
            AgentflowId = Guid.Empty,
            AgentflowRevision = 0,
            EdgeId = edge.EdgeId,
            SourceNodeId = edge.SourceNodeId,
            TargetNodeId = edge.TargetNodeId,
            IsFanInBarrier = edge.IsFanInBarrier,
            IsSwitchDefault = edge.IsSwitchDefault,
            SwitchCaseOrder = edge.SwitchCaseOrder,
            ChoiceId = edge.ChoiceId,
            Label = edge.Label,
            ConditionJson = AgentflowDefinitionJson.WriteConditions(conditions),
        };

    public static AgentflowDefinition FromEntities(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyList<AgentflowBlockMember> members
    )
    {
        var nodeOptions = ReadNodeOptionsMap(nodes);
        var edgeConditions = edges
            .GroupBy(edge => edge.EdgeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<AgentflowConditionRule>)AgentflowDefinitionJson.ReadConditions(group.First()),
                StringComparer.Ordinal
            );
        return new AgentflowDefinition(nodes, edges, members, nodeOptions, edgeConditions);
    }

    public static AgentflowDefinitionDto ToDto(AgentflowDefinition definition)
    {
        var membersByNode = MapMembersByNode(definition.Members);
        return new AgentflowDefinitionDto(
            definition.Nodes.Select(node => MapNode(node, membersByNode, definition.NodeOptions)).ToArray(),
            MapEdges(definition.Edges, definition.EdgeConditions)
        );
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<AgentflowBlockMemberDto>> MapMembersByNode(
        IReadOnlyList<AgentflowBlockMember> members
    ) =>
        members
            .OrderBy(member => member.SortOrder)
            .GroupBy(member => member.NodeId)
            .ToDictionary(
                group => group.Key,
                group =>
                    (IReadOnlyList<AgentflowBlockMemberDto>)
                        group
                            .Select(member => new AgentflowBlockMemberDto(
                                member.MemberId,
                                ToBlockMemberKind(member.Kind),
                                member.RelateId == Guid.Empty ? null : member.RelateId,
                                member.Name,
                                member.Instructions,
                                member.IsManager
                            ))
                            .ToArray()
            );

    private static AgentflowEdgeDto[] MapEdges(
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, IReadOnlyList<AgentflowConditionRule>> edgeConditions
    ) => edges.Select(edge => MapEdge(edge, edgeConditions.GetValueOrDefault(edge.EdgeId, []))).ToArray();

    private static AgentflowEdgeDto MapEdge(AgentflowEdge edge, IReadOnlyList<AgentflowConditionRule> conditions) =>
        new(
            edge.EdgeId,
            edge.SourceNodeId,
            edge.TargetNodeId,
            edge.IsFanInBarrier,
            edge.IsSwitchDefault,
            edge.SwitchCaseOrder,
            edge.ChoiceId,
            edge.Label,
            conditions
                .Select(condition => new AgentflowConditionRuleDto(
                    ToDtoConditionField(condition.Field),
                    condition.Value,
                    condition.Path
                ))
                .ToArray()
        );

    private static IReadOnlyDictionary<string, AgentflowNodeOptions> ReadNodeOptionsMap(
        IReadOnlyList<AgentflowNode> nodes
    ) =>
        nodes
            .Select(node => (node.NodeId, Options: ReadNodeOptions(node)))
            .Where(item => item.Options is not null)
            .GroupBy(item => item.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Options!, StringComparer.Ordinal);

    private static AgentflowNodeOptions? ReadNodeOptions(AgentflowNode node) =>
        node.Kind switch
        {
            AgentflowNodeKind.HumanGate => AgentflowDefinitionJson.ReadOptions<HumanGateOptions>(node),
            AgentflowNodeKind.CheckpointMarker => AgentflowDefinitionJson.ReadOptions<CheckpointMarkerOptions>(node),
            AgentflowNodeKind.Output => AgentflowDefinitionJson.ReadOptions<OutputOptions>(node),
            AgentflowNodeKind.HandoffBlock => AgentflowDefinitionJson.ReadOptions<HandoffBlockOptions>(node),
            AgentflowNodeKind.GroupChatBlock => AgentflowDefinitionJson.ReadOptions<GroupChatBlockOptions>(node),
            AgentflowNodeKind.MagenticBlock => AgentflowDefinitionJson.ReadOptions<MagenticBlockOptions>(node),
            _ => null,
        };

    private static AgentflowNode MapNode(AgentflowNodeDto node) =>
        node switch
        {
            AgentflowInputNodeDto => CreateNode(node, AgentflowNodeKind.Input, AgentflowDefinitionJson.EmptyOptions),
            AgentflowAgentNodeDto agent => MapReferenceNode(
                agent,
                AgentflowNodeKind.Agent,
                agent.RelateId,
                agent.Instructions
            ),
            AgentflowWorkflowAsAgentNodeDto workflow => MapReferenceNode(
                workflow,
                AgentflowNodeKind.WorkflowAsAgent,
                workflow.RelateId,
                workflow.Instructions
            ),
            AgentflowPromptAdapterNodeDto adapter => CreateNode(
                adapter,
                AgentflowNodeKind.PromptAdapter,
                AgentflowDefinitionJson.EmptyOptions,
                instructions: adapter.Instructions
            ),
            AgentflowClearMessagesNodeDto => CreateNode(
                node,
                AgentflowNodeKind.ClearMessages,
                AgentflowDefinitionJson.EmptyOptions
            ),
            AgentflowHumanGateNodeDto gate => MapHumanGateNode(gate),
            AgentflowCheckpointMarkerNodeDto checkpoint => CreateNode(
                checkpoint,
                AgentflowNodeKind.CheckpointMarker,
                AgentflowDefinitionJson.WriteOptions(new CheckpointMarkerOptions(checkpoint.CheckpointName))
            ),
            AgentflowOutputNodeDto output => MapOutputNode(output),
            AgentflowConcurrentBlockNodeDto block => CreateNode(
                block,
                AgentflowNodeKind.ConcurrentBlock,
                AgentflowDefinitionJson.EmptyOptions,
                instructions: block.Instructions
            ),
            AgentflowHandoffBlockNodeDto block => MapHandoffBlockNode(block),
            AgentflowGroupChatBlockNodeDto block => MapGroupChatBlockNode(block),
            AgentflowMagenticBlockNodeDto block => MapMagenticBlockNode(block),
            _ => throw new AgwException(ErrorCodes.InvalidParam),
        };

    private static AgentflowNode MapReferenceNode(
        AgentflowNodeDto node,
        AgentflowNodeKind kind,
        Guid? relateId,
        string? instructions
    ) => CreateNode(node, kind, AgentflowDefinitionJson.EmptyOptions, relateId, instructions);

    private static AgentflowNode MapHumanGateNode(AgentflowHumanGateNodeDto gate) =>
        CreateNode(
            gate,
            AgentflowNodeKind.HumanGate,
            AgentflowDefinitionJson.WriteOptions(
                new HumanGateOptions(
                    gate.HumanPrompt,
                    gate.Choices.Select(choice => new AgentflowGateChoice(choice.ChoiceId, choice.Label)).ToArray(),
                    gate.AllowResponseText
                )
            )
        );

    private static AgentflowNode MapOutputNode(AgentflowOutputNodeDto output) =>
        CreateNode(
            output,
            AgentflowNodeKind.Output,
            AgentflowDefinitionJson.WriteOptions(new OutputOptions(output.EnableSummary)),
            instructions: output.Instructions,
            summaryModelProviderId: output.SummaryModelProviderId
        );

    private static AgentflowNode MapHandoffBlockNode(AgentflowHandoffBlockNodeDto block) =>
        CreateNode(
            block,
            AgentflowNodeKind.HandoffBlock,
            AgentflowDefinitionJson.WriteOptions(
                new HandoffBlockOptions(
                    block.HandoffInstructions,
                    block.EnableReturnToPrevious,
                    block.Autonomous,
                    block.AutonomousTurnLimit,
                    block.ContinuationPrompt
                )
            ),
            instructions: block.Instructions
        );

    private static AgentflowNode MapGroupChatBlockNode(AgentflowGroupChatBlockNodeDto block) =>
        CreateNode(
            block,
            AgentflowNodeKind.GroupChatBlock,
            AgentflowDefinitionJson.WriteOptions(new GroupChatBlockOptions(block.MaxRounds)),
            instructions: block.Instructions
        );

    private static AgentflowNode MapMagenticBlockNode(AgentflowMagenticBlockNodeDto block) =>
        CreateNode(
            block,
            AgentflowNodeKind.MagenticBlock,
            AgentflowDefinitionJson.WriteOptions(
                new MagenticBlockOptions(block.MaxRounds, block.MaxStalls, block.MaxResets, block.RequirePlanSignoff)
            ),
            instructions: block.Instructions
        );

    private static AgentflowNode CreateNode(
        AgentflowNodeDto node,
        AgentflowNodeKind kind,
        string configJson,
        Guid? relateId = null,
        string? instructions = null,
        Guid? summaryModelProviderId = null
    ) =>
        new()
        {
            AgentflowId = Guid.Empty,
            AgentflowRevision = 0,
            NodeId = node.NodeId,
            Kind = kind,
            RelateId = relateId,
            Name = node.Name,
            PositionX = node.Position.X,
            PositionY = node.Position.Y,
            RoutingStrategy = node.RoutingStrategy,
            SummaryModelProviderId = summaryModelProviderId,
            Instructions = instructions,
            ConfigJson = configJson,
        };

    private static IEnumerable<AgentflowBlockMemberDto> EnumerateMembers(AgentflowNodeDto node) =>
        node switch
        {
            AgentflowConcurrentBlockNodeDto block => block.Members,
            AgentflowHandoffBlockNodeDto block => block.Members,
            AgentflowGroupChatBlockNodeDto block => block.Members,
            AgentflowMagenticBlockNodeDto block => block.Members,
            _ => [],
        };

    private static AgentflowNodeKind ToNodeKind(AgentflowBlockMemberKind kind) =>
        kind switch
        {
            AgentflowBlockMemberKind.Agent => AgentflowNodeKind.Agent,
            AgentflowBlockMemberKind.WorkflowAsAgent => AgentflowNodeKind.WorkflowAsAgent,
            _ => throw new AgwException(ErrorCodes.InvalidParam),
        };

    private static AgentflowBlockMemberKind ToBlockMemberKind(AgentflowNodeKind kind) =>
        kind switch
        {
            AgentflowNodeKind.Agent => AgentflowBlockMemberKind.Agent,
            AgentflowNodeKind.WorkflowAsAgent => AgentflowBlockMemberKind.WorkflowAsAgent,
            _ => throw new AgwException(ErrorCodes.InvalidParam),
        };

    private static string ToDomainConditionField(AgentflowConditionField field) =>
        field switch
        {
            AgentflowConditionField.Contains => "contains",
            AgentflowConditionField.NotContains => "notContains",
            AgentflowConditionField.Equals => "equals",
            AgentflowConditionField.Author => "author",
            AgentflowConditionField.Role => "role",
            AgentflowConditionField.MinMessages => "minMessages",
            AgentflowConditionField.Result => "result",
            _ => throw new AgwException(ErrorCodes.InvalidParam),
        };

    private static AgentflowConditionField ToDtoConditionField(string field) =>
        field switch
        {
            "contains" => AgentflowConditionField.Contains,
            "notContains" => AgentflowConditionField.NotContains,
            "equals" => AgentflowConditionField.Equals,
            "author" => AgentflowConditionField.Author,
            "role" => AgentflowConditionField.Role,
            "minMessages" => AgentflowConditionField.MinMessages,
            "result" => AgentflowConditionField.Result,
            _ => throw new AgwException(ErrorCodes.InvalidParam),
        };

    private static AgentflowNodeDto MapNode(
        AgentflowNode node,
        IReadOnlyDictionary<string, IReadOnlyList<AgentflowBlockMemberDto>> membersByNode,
        IReadOnlyDictionary<string, AgentflowNodeOptions> optionsByNode
    )
    {
        var position = new AgentflowPositionDto(node.PositionX, node.PositionY);
        var members = membersByNode.GetValueOrDefault(node.NodeId, []);
        var options = optionsByNode.GetValueOrDefault(node.NodeId);

        return node.Kind switch
        {
            AgentflowNodeKind.Input => new AgentflowInputNodeDto(
                node.NodeId,
                node.Name,
                position,
                node.RoutingStrategy
            ),
            AgentflowNodeKind.Agent => MapAgentNode(node, position),
            AgentflowNodeKind.WorkflowAsAgent => MapWorkflowAsAgentNode(node, position),
            AgentflowNodeKind.PromptAdapter => new AgentflowPromptAdapterNodeDto(
                node.NodeId,
                node.Name,
                position,
                node.RoutingStrategy,
                node.Instructions
            ),
            AgentflowNodeKind.ClearMessages => new AgentflowClearMessagesNodeDto(
                node.NodeId,
                node.Name,
                position,
                node.RoutingStrategy
            ),
            AgentflowNodeKind.HumanGate => MapHumanGate(node, position, options as HumanGateOptions),
            AgentflowNodeKind.CheckpointMarker => MapCheckpointMarker(
                node,
                position,
                options as CheckpointMarkerOptions
            ),
            AgentflowNodeKind.Output => MapOutput(node, position, options as OutputOptions),
            AgentflowNodeKind.ConcurrentBlock
            or AgentflowNodeKind.HandoffBlock
            or AgentflowNodeKind.GroupChatBlock
            or AgentflowNodeKind.MagenticBlock => MapBlockNode(node, position, members, options),
            _ => throw new AgwException(ErrorCodes.InvalidParam),
        };
    }

    private static AgentflowAgentNodeDto MapAgentNode(AgentflowNode node, AgentflowPositionDto position) =>
        new(node.NodeId, node.Name, position, node.RoutingStrategy, node.RelateId, node.Instructions);

    private static AgentflowWorkflowAsAgentNodeDto MapWorkflowAsAgentNode(
        AgentflowNode node,
        AgentflowPositionDto position
    ) => new(node.NodeId, node.Name, position, node.RoutingStrategy, node.RelateId, node.Instructions);

    private static AgentflowCheckpointMarkerNodeDto MapCheckpointMarker(
        AgentflowNode node,
        AgentflowPositionDto position,
        CheckpointMarkerOptions? options
    ) => new(node.NodeId, node.Name, position, node.RoutingStrategy, options?.CheckpointName);

    private static AgentflowNodeDto MapBlockNode(
        AgentflowNode node,
        AgentflowPositionDto position,
        IReadOnlyList<AgentflowBlockMemberDto> members,
        AgentflowNodeOptions? options
    ) =>
        node.Kind switch
        {
            AgentflowNodeKind.ConcurrentBlock => new AgentflowConcurrentBlockNodeDto(
                node.NodeId,
                node.Name,
                position,
                node.RoutingStrategy,
                node.Instructions,
                members
            ),
            AgentflowNodeKind.HandoffBlock => MapHandoffBlock(node, position, members, options as HandoffBlockOptions),
            AgentflowNodeKind.GroupChatBlock => MapGroupChatBlock(
                node,
                position,
                members,
                options as GroupChatBlockOptions
            ),
            AgentflowNodeKind.MagenticBlock => MapMagenticBlock(
                node,
                position,
                members,
                options as MagenticBlockOptions
            ),
            _ => throw new AgwException(ErrorCodes.InvalidParam),
        };

    private static AgentflowGroupChatBlockNodeDto MapGroupChatBlock(
        AgentflowNode node,
        AgentflowPositionDto position,
        IReadOnlyList<AgentflowBlockMemberDto> members,
        GroupChatBlockOptions? options
    ) => new(node.NodeId, node.Name, position, node.RoutingStrategy, node.Instructions, members, options?.MaxRounds);

    private static AgentflowHumanGateNodeDto MapHumanGate(
        AgentflowNode node,
        AgentflowPositionDto position,
        HumanGateOptions? options
    )
    {
        return new AgentflowHumanGateNodeDto(
            node.NodeId,
            node.Name,
            position,
            node.RoutingStrategy,
            options?.HumanPrompt,
            options?.Choices.Select(choice => new AgentflowGateChoiceDto(choice.ChoiceId, choice.Label)).ToArray()
                ?? [],
            options?.AllowResponseText ?? false
        );
    }

    private static AgentflowOutputNodeDto MapOutput(
        AgentflowNode node,
        AgentflowPositionDto position,
        OutputOptions? options
    ) =>
        new(
            node.NodeId,
            node.Name,
            position,
            node.RoutingStrategy,
            options?.EnableSummary ?? false,
            node.SummaryModelProviderId,
            node.Instructions
        );

    private static AgentflowHandoffBlockNodeDto MapHandoffBlock(
        AgentflowNode node,
        AgentflowPositionDto position,
        IReadOnlyList<AgentflowBlockMemberDto> members,
        HandoffBlockOptions? options
    )
    {
        return new AgentflowHandoffBlockNodeDto(
            node.NodeId,
            node.Name,
            position,
            node.RoutingStrategy,
            node.Instructions,
            members,
            options?.HandoffInstructions,
            options?.EnableReturnToPrevious,
            options?.Autonomous,
            options?.AutonomousTurnLimit,
            options?.ContinuationPrompt
        );
    }

    private static AgentflowMagenticBlockNodeDto MapMagenticBlock(
        AgentflowNode node,
        AgentflowPositionDto position,
        IReadOnlyList<AgentflowBlockMemberDto> members,
        MagenticBlockOptions? options
    )
    {
        return new AgentflowMagenticBlockNodeDto(
            node.NodeId,
            node.Name,
            position,
            node.RoutingStrategy,
            node.Instructions,
            members,
            options?.MaxRounds,
            options?.MaxStalls,
            options?.MaxResets,
            options?.RequirePlanSignoff
        );
    }
}
