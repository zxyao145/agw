using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Tests;

public sealed class AgentflowBehaviorValidateTests
{
    [Theory]
    [InlineData("name-required")]
    [InlineData("input-invalid")]
    [InlineData("duplicate-id")]
    [InlineData("runner-required")]
    [InlineData("node-unreachable")]
    [InlineData("edge-endpoint-missing")]
    [InlineData("routing-invalid")]
    [InlineData("switch-case-required")]
    [InlineData("switch-condition-required")]
    [InlineData("switch-default-duplicate")]
    [InlineData("switch-order-duplicate")]
    [InlineData("condition-invalid")]
    [InlineData("choice-required")]
    [InlineData("edge-choice-invalid")]
    [InlineData("block-member-count")]
    [InlineData("block-manager-invalid")]
    [InlineData("cycle-exit-required")]
    [InlineData("cycle-barrier-invalid")]
    [InlineData("summary-output-count")]
    public void Validate_EachBehaviorRule_ReportsExpectedProblem(string code)
    {
        var (name, definition, expectedMessage, expectedNodeId, expectedEdgeId) = CreateInvalidDefinition(code);

        var problems = AgentflowBehavior.Validate(name, definition);

        var problem = Assert.Single(problems);
        Assert.Equal(code, problem.Code);
        Assert.Equal(expectedMessage, problem.Message);
        Assert.Equal(expectedNodeId, problem.NodeId);
        Assert.Equal(expectedEdgeId, problem.EdgeId);
    }

    [Fact]
    public void Validate_TwoIndependentProblems_ReportsBoth()
    {
        var definition = CreateValidDefinition();
        var extraNode = CreateNode("unused", AgentflowNodeKind.CheckpointMarker);
        var nodes = definition.Nodes.Append(extraNode).ToArray();
        var agent = nodes.Single(node => node.Kind == AgentflowNodeKind.Agent);
        agent.RoutingStrategy = AgentflowRoutingStrategy.Switch;
        definition.Edges.Single(edge => edge.EdgeId == "agent-output").ConditionJson =
            AgentflowDefinitionJson.WriteConditions([new AgentflowConditionRule("contains", "approved", "")]);
        var edges = definition
            .Edges.Append(
                new AgentflowEdge
                {
                    EdgeId = "switch-to-output",
                    SourceNodeId = agent.NodeId,
                    TargetNodeId = "output",
                    SwitchCaseOrder = 1,
                    ConditionJson = AgentflowDefinitionJson.WriteConditions([]),
                }
            )
            .ToArray();

        var problems = AgentflowBehavior.Validate("Review", AgentflowDefinitionMapper.FromEntities(nodes, edges, []));

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, problem => problem.Code == "node-unreachable" && problem.NodeId == "unused");
        Assert.Contains(
            problems,
            problem => problem.Code == "switch-condition-required" && problem.EdgeId == "switch-to-output"
        );
    }

    [Fact]
    public void Validate_ValidDefinition_ReportsNothing()
    {
        var problems = AgentflowBehavior.Validate("Review", CreateValidDefinition());

        Assert.Empty(problems);
    }

    [Fact]
    public void Validate_CycleLeavingThroughChoice_ReportsNothing()
    {
        var gate = CreateNode("gate", AgentflowNodeKind.HumanGate, AgentflowRoutingStrategy.Choice);
        gate.ConfigJson = AgentflowDefinitionJson.WriteOptions(
            new HumanGateOptions("Review", [new AgentflowGateChoice("continue", "Continue")], false)
        );
        var nodes = new[]
        {
            CreateNode("input", AgentflowNodeKind.Input, AgentflowRoutingStrategy.FanOut),
            gate,
            CreateNode("agent", AgentflowNodeKind.Agent),
            CreateNode("output", AgentflowNodeKind.Output),
        };
        var edges = new[]
        {
            CreateEdge("input-gate", "input", "gate"),
            CreateEdge("gate-agent", "gate", "agent", choiceId: "continue"),
            CreateEdge("agent-gate", "agent", "gate"),
            CreateEdge("gate-output", "gate", "output", choiceId: "continue"),
        };

        var problems = AgentflowBehavior.Validate("Review", AgentflowDefinitionMapper.FromEntities(nodes, edges, []));

        Assert.Empty(problems);
    }

    [Fact]
    public void Validate_LabeledWaitingEdge_ReportsNothing()
    {
        var definition = CreateValidDefinition();
        var waitEdge = definition.Edges.Single(edge => edge.EdgeId == "agent-output");
        waitEdge.IsFanInBarrier = true;
        waitEdge.Label = "Wait";

        var problems = AgentflowBehavior.Validate("Review", definition);

        Assert.Empty(problems);
    }

    [Fact]
    public void Validate_InputWithImportedNodeId_LocatesTheInputNode()
    {
        var definition = CreateValidDefinition();
        var input = definition.Nodes.Single(node => node.Kind == AgentflowNodeKind.Input);
        input.NodeId = "entry";
        foreach (var edge in definition.Edges.Where(edge => edge.SourceNodeId == "input"))
        {
            edge.SourceNodeId = input.NodeId;
        }

        var problem = Assert.Single(AgentflowBehavior.Validate("Review", definition));

        Assert.Equal("input-invalid", problem.Code);
        Assert.Equal("entry", problem.NodeId);
    }

    [Fact]
    public void Validate_DirectEdgeSwitchOrder_ReportsRoutingProblem()
    {
        var definition = CreateValidDefinition();
        definition.Edges.Single(edge => edge.EdgeId == "agent-output").SwitchCaseOrder = 2;

        var problem = Assert.Single(AgentflowBehavior.Validate("Review", definition));

        Assert.Equal("routing-invalid", problem.Code);
        Assert.Equal("The connection from Runner to Output has routing fields that cannot be used.", problem.Message);
        Assert.Equal("agent-output", problem.EdgeId);
    }

    [Fact]
    public void Validate_SwitchElseOrder_ReportsRoutingProblem()
    {
        var definition = CreateValidDefinition();
        var agent = definition.Nodes.Single(node => node.Kind == AgentflowNodeKind.Agent);
        agent.RoutingStrategy = AgentflowRoutingStrategy.Switch;
        var caseEdge = definition.Edges.Single(edge => edge.EdgeId == "agent-output");
        caseEdge.ConditionJson = AgentflowDefinitionJson.WriteConditions([
            new AgentflowConditionRule("contains", "done", ""),
        ]);
        caseEdge.SwitchCaseOrder = 1;
        var edges = definition
            .Edges.Append(
                new AgentflowEdge
                {
                    EdgeId = "agent-else",
                    SourceNodeId = "agent",
                    TargetNodeId = "output",
                    IsSwitchDefault = true,
                    SwitchCaseOrder = 5,
                    ConditionJson = AgentflowDefinitionJson.WriteConditions([]),
                }
            )
            .ToArray();
        definition = AgentflowDefinitionMapper.FromEntities(definition.Nodes, edges, definition.Members);

        var problem = Assert.Single(AgentflowBehavior.Validate("Review", definition));

        Assert.Equal("routing-invalid", problem.Code);
        Assert.Equal("agent-else", problem.EdgeId);
    }

    [Fact]
    public void Validate_DuplicateEdgeId_UsesEndpointNames()
    {
        var definition = CreateValidDefinition();
        var duplicate = CreateEdge("agent-output", "agent", "output");
        var edges = definition.Edges.Append(duplicate).ToArray();
        definition = AgentflowDefinitionMapper.FromEntities(definition.Nodes, edges, definition.Members);

        var problem = Assert.Single(AgentflowBehavior.Validate("Review", definition));

        Assert.Equal("duplicate-id", problem.Code);
        Assert.Equal("The connection from Runner to Output is used more than once.", problem.Message);
        Assert.Equal("agent-output", problem.EdgeId);
    }

    [Fact]
    public void Validate_DuplicateBlockMemberId_UsesBlockName()
    {
        var definition = CreateValidDefinition();
        var concurrent = CreateNode("concurrent", AgentflowNodeKind.ConcurrentBlock);
        concurrent.Name = "Concurrent";
        var nodes = definition.Nodes.Append(concurrent).ToArray();
        var edges = definition
            .Edges.Select(edge => edge.EdgeId == "agent-output" ? CreateEdge(edge.EdgeId, "agent", "concurrent") : edge)
            .Append(CreateEdge("concurrent-output", "concurrent", "output"))
            .ToArray();
        var members = new[]
        {
            CreateMember("concurrent", "member", isManager: false),
            CreateMember("concurrent", "member", isManager: false),
        };
        definition = AgentflowDefinitionMapper.FromEntities(nodes, edges, members);

        var problem = Assert.Single(AgentflowBehavior.Validate("Review", definition));

        Assert.Equal("duplicate-id", problem.Code);
        Assert.Equal("Concurrent has a member that is used more than once.", problem.Message);
        Assert.Equal("concurrent", problem.NodeId);
    }

    private static AgentflowDefinition CreateValidDefinition()
    {
        var agent = CreateNode("agent", AgentflowNodeKind.Agent);
        agent.Name = "Runner";
        var nodes = new[]
        {
            CreateNode("input", AgentflowNodeKind.Input, AgentflowRoutingStrategy.FanOut),
            agent,
            CreateOutputNode(),
        };
        var edges = new[]
        {
            CreateEdge("input-agent", "input", "agent"),
            CreateEdge("agent-output", "agent", "output"),
        };
        return AgentflowDefinitionMapper.FromEntities(nodes, edges, []);
    }

    private static (
        string Name,
        AgentflowDefinition Definition,
        string Message,
        string? NodeId,
        string? EdgeId
    ) CreateInvalidDefinition(string code)
    {
        var definition = CreateValidDefinition();
        var nodes = definition.Nodes.ToList();
        var edges = definition.Edges.ToList();
        var members = new List<AgentflowBlockMember>();
        var name = "Review";
        string message;
        string? nodeId = null;
        string? edgeId = null;
        var agent = nodes.Single(node => node.Kind == AgentflowNodeKind.Agent);

        switch (code)
        {
            case "name-required":
                name = " ";
                message = "The Agentflow needs a name of at most 200 characters.";
                break;
            case "input-invalid":
            {
                var secondInput = CreateNode("input-2", AgentflowNodeKind.Input);
                nodes.Add(secondInput);
                edges.Add(CreateEdge("input-second", "input", "input-2"));
                edges.Add(CreateEdge("second-agent", "input-2", "agent"));
                nodeId = "input";
                message = "The flow needs exactly one Input node with no incoming connections.";
                break;
            }
            case "duplicate-id":
            {
                nodes.Add(CreateNode("agent", AgentflowNodeKind.PromptAdapter));
                nodeId = "agent";
                message = "Runner is used more than once.";
                break;
            }
            case "runner-required":
                nodes.Remove(agent);
                edges = [CreateEdge("input-output", "input", "output")];
                message = "Add an Agent, a Workflow, or a Block with members.";
                break;
            case "node-unreachable":
                nodes.Add(CreateNode("unused", AgentflowNodeKind.CheckpointMarker));
                nodeId = "unused";
                message = "unused is not reachable from Input.";
                break;
            case "edge-endpoint-missing":
                edges.Add(CreateEdge("missing-target", "agent", "missing"));
                edgeId = "missing-target";
                message = "A connection points to a node that does not exist.";
                break;
            case "routing-invalid":
                agent.RoutingStrategy = (AgentflowRoutingStrategy)99;
                nodeId = "agent";
                message = "Runner cannot hand off this way.";
                break;
            case "switch-case-required":
                agent.RoutingStrategy = AgentflowRoutingStrategy.Switch;
                edges.Single(edge => edge.EdgeId == "agent-output").IsSwitchDefault = true;
                nodeId = "agent";
                message = "Runner needs at least one branch with a condition.";
                break;
            case "switch-condition-required":
                agent.RoutingStrategy = AgentflowRoutingStrategy.Switch;
                edges.Single(edge => edge.EdgeId == "agent-output").ConditionJson = Condition("contains", "done");
                edges.Single(edge => edge.EdgeId == "agent-output").SwitchCaseOrder = 1;
                edges.Add(
                    new AgentflowEdge
                    {
                        EdgeId = "missing-condition",
                        SourceNodeId = "agent",
                        TargetNodeId = "output",
                        SwitchCaseOrder = 2,
                        ConditionJson = AgentflowDefinitionJson.WriteConditions([]),
                    }
                );
                edgeId = "missing-condition";
                message = "The branch from Runner to Output needs a condition.";
                break;
            case "switch-default-duplicate":
            {
                agent.RoutingStrategy = AgentflowRoutingStrategy.Switch;
                var switchEdge = edges.Single(edge => edge.EdgeId == "agent-output");
                switchEdge.ConditionJson = Condition("contains", "done");
                switchEdge.SwitchCaseOrder = 1;
                edges.AddRange([
                    new AgentflowEdge
                    {
                        EdgeId = "else-1",
                        SourceNodeId = "agent",
                        TargetNodeId = "output",
                        IsSwitchDefault = true,
                        ConditionJson = "[]",
                    },
                    new AgentflowEdge
                    {
                        EdgeId = "else-2",
                        SourceNodeId = "agent",
                        TargetNodeId = "output",
                        IsSwitchDefault = true,
                        ConditionJson = "[]",
                    },
                ]);
                nodeId = "agent";
                message = "Runner has more than one Else branch.";
                break;
            }
            case "switch-order-duplicate":
            {
                agent.RoutingStrategy = AgentflowRoutingStrategy.Switch;
                var firstCase = edges.Single(edge => edge.EdgeId == "agent-output");
                firstCase.ConditionJson = Condition("contains", "done");
                firstCase.SwitchCaseOrder = 1;
                edges.Add(
                    new AgentflowEdge
                    {
                        EdgeId = "same-order",
                        SourceNodeId = "agent",
                        TargetNodeId = "output",
                        SwitchCaseOrder = 1,
                        ConditionJson = Condition("equals", "done"),
                    }
                );
                nodeId = "agent";
                message = "Runner has two branches in the same position.";
                break;
            }
            case "condition-invalid":
                edges.Single(edge => edge.EdgeId == "agent-output").ConditionJson = Condition("always", "true");
                edgeId = "agent-output";
                message = "The connection from Runner to Output has a condition that cannot be used.";
                break;
            case "choice-required":
            {
                var emptyGate = CreateNode("gate", AgentflowNodeKind.HumanGate, AgentflowRoutingStrategy.Choice);
                emptyGate.ConfigJson = AgentflowDefinitionJson.WriteOptions(new HumanGateOptions(null, [], false));
                nodes.Add(emptyGate);
                edges.Add(CreateEdge("input-gate", "input", "gate"));
                nodeId = "gate";
                message = "gate needs at least one choice, and every choice needs a label.";
                break;
            }
            case "edge-choice-invalid":
            {
                var gate = CreateNode("gate", AgentflowNodeKind.HumanGate, AgentflowRoutingStrategy.Choice);
                gate.ConfigJson = AgentflowDefinitionJson.WriteOptions(
                    new HumanGateOptions("Review", [new AgentflowGateChoice("continue", "Continue")], false)
                );
                nodes.Add(gate);
                edges.Single(edge => edge.EdgeId == "agent-output").TargetNodeId = "gate";
                edges.Add(CreateEdge("gate-output", "gate", "output", "missing"));
                edgeId = "gate-output";
                message = "The connection from gate to Output is not bound to a choice of gate.";
                break;
            }
            case "block-member-count":
            {
                var concurrent = CreateNode("concurrent", AgentflowNodeKind.ConcurrentBlock);
                nodes.Add(concurrent);
                edges.Single(edge => edge.EdgeId == "agent-output").TargetNodeId = "concurrent";
                edges.Add(CreateEdge("concurrent-output", "concurrent", "output"));
                nodeId = "concurrent";
                message = "concurrent needs at least one member.";
                break;
            }
            case "block-manager-invalid":
            {
                var magentic = CreateNode("magentic", AgentflowNodeKind.MagenticBlock);
                nodes.Add(magentic);
                edges.Single(edge => edge.EdgeId == "agent-output").TargetNodeId = "magentic";
                edges.Add(CreateEdge("magentic-output", "magentic", "output"));
                members.AddRange([
                    CreateMember("magentic", "manager-1", isManager: true),
                    CreateMember("magentic", "manager-2", isManager: true),
                ]);
                nodeId = "magentic";
                message = "magentic needs exactly one manager.";
                break;
            }
            case "cycle-exit-required":
            {
                edges =
                [
                    CreateEdge("input-agent", "input", "agent"),
                    CreateEdge("agent-loop", "agent", "agent"),
                    CreateEdge("input-output", "input", "output"),
                ];
                nodeId = "agent";
                message = "The loop through Runner needs a branch or a choice that leaves it.";
                break;
            }
            case "cycle-barrier-invalid":
            {
                nodes =
                [
                    CreateNode("input", AgentflowNodeKind.Input, AgentflowRoutingStrategy.FanOut),
                    CreateNode("runner", AgentflowNodeKind.Agent),
                    CreateNode("loop-a", AgentflowNodeKind.PromptAdapter),
                    CreateNode("loop-b", AgentflowNodeKind.PromptAdapter, AgentflowRoutingStrategy.Switch),
                    CreateNode("output", AgentflowNodeKind.Output),
                ];
                edges =
                [
                    CreateEdge("input-runner", "input", "runner"),
                    new AgentflowEdge
                    {
                        EdgeId = "runner-loop-a",
                        SourceNodeId = "runner",
                        TargetNodeId = "loop-a",
                        IsFanInBarrier = true,
                        ConditionJson = "[]",
                    },
                    CreateEdge("loop-a-loop-b", "loop-a", "loop-b"),
                    new AgentflowEdge
                    {
                        EdgeId = "loop-b-loop-a",
                        SourceNodeId = "loop-b",
                        TargetNodeId = "loop-a",
                        SwitchCaseOrder = 1,
                        ConditionJson = Condition("contains", "again"),
                    },
                    new AgentflowEdge
                    {
                        EdgeId = "loop-b-output",
                        SourceNodeId = "loop-b",
                        TargetNodeId = "output",
                        SwitchCaseOrder = 2,
                        ConditionJson = Condition("contains", "done"),
                    },
                ];
                edgeId = "runner-loop-a";
                message = "loop-a is in a loop and cannot wait for runner.";
                break;
            }
            case "summary-output-count":
            {
                var summaryOutput = nodes.Single(node => node.Kind == AgentflowNodeKind.Output);
                summaryOutput.ConfigJson = AgentflowDefinitionJson.WriteOptions(new OutputOptions(true));
                nodes.Add(CreateNode("output-2", AgentflowNodeKind.Output));
                edges.Add(CreateEdge("agent-output-2", "agent", "output-2"));
                nodeId = "output";
                message = "Only one Output can exist when a summary is written.";
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown test code '{code}'.");
        }

        return (name, AgentflowDefinitionMapper.FromEntities(nodes, edges, members), message, nodeId, edgeId);
    }

    private static string Condition(string field, string value) =>
        AgentflowDefinitionJson.WriteConditions([new AgentflowConditionRule(field, value, "")]);

    private static AgentflowNode CreateOutputNode()
    {
        var output = CreateNode("output", AgentflowNodeKind.Output);
        output.Name = "Output";
        return output;
    }

    private static AgentflowNode CreateNode(
        string nodeId,
        AgentflowNodeKind kind,
        AgentflowRoutingStrategy routingStrategy = AgentflowRoutingStrategy.Direct
    ) =>
        new()
        {
            NodeId = nodeId,
            Kind = kind,
            Name = nodeId,
            RoutingStrategy = routingStrategy,
            ConfigJson = AgentflowDefinitionJson.EmptyOptions,
        };

    private static AgentflowEdge CreateEdge(string edgeId, string source, string target, string? choiceId = null) =>
        new()
        {
            EdgeId = edgeId,
            SourceNodeId = source,
            TargetNodeId = target,
            ConditionJson = AgentflowDefinitionJson.WriteConditions([]),
            ChoiceId = choiceId,
        };

    private static AgentflowBlockMember CreateMember(string nodeId, string memberId, bool isManager) =>
        new()
        {
            NodeId = nodeId,
            MemberId = memberId,
            Kind = AgentflowNodeKind.Agent,
            RelateId = Guid.CreateVersion7(),
            Name = memberId,
            IsManager = isManager,
        };
}
