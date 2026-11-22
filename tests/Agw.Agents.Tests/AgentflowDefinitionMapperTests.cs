using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public sealed class AgentflowDefinitionMapperTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
    };

    [Fact]
    public void ToDefinition_EveryNodeKind_RoundTripsThroughToDto()
    {
        const string json = """
            {
              "nodes": [
                {"nodeId":"input","kind":"input","name":"Input","position":{"x":80,"y":80},"routingStrategy":"fanOut"},
                {"nodeId":"agent","kind":"agent","name":"Agent","position":{"x":200,"y":80},"routingStrategy":"direct","relateId":"0199c0de-0000-7000-8000-000000000001","instructions":"Agent instructions"},
                {"nodeId":"workflow","kind":"workflowAsAgent","name":"Workflow","position":{"x":320,"y":80},"routingStrategy":"direct","relateId":"0199c0de-0000-7000-8000-000000000002","instructions":"Workflow instructions"},
                {"nodeId":"adapter","kind":"promptAdapter","name":"Adapter","position":{"x":440,"y":80},"routingStrategy":"direct","instructions":"Adapter instructions"},
                {"nodeId":"clear","kind":"clearMessages","name":"Clear","position":{"x":560,"y":80},"routingStrategy":"direct"},
                {"nodeId":"gate","kind":"humanGate","name":"Gate","position":{"x":680,"y":80},"routingStrategy":"choice","humanPrompt":"Review","choices":[{"choiceId":"approve","label":"Approve"}],"allowResponseText":true},
                {"nodeId":"checkpoint","kind":"checkpointMarker","name":"Checkpoint","position":{"x":800,"y":80},"routingStrategy":"direct","checkpointName":"reviewed"},
                {"nodeId":"output","kind":"output","name":"Output","position":{"x":920,"y":80},"routingStrategy":"direct","enableSummary":true,"summaryModelProviderId":"0199c0de-0000-7000-8000-000000000003","instructions":"Summary instructions"},
                {"nodeId":"concurrent","kind":"concurrentBlock","name":"Concurrent","position":{"x":1040,"y":80},"routingStrategy":"direct","instructions":"Concurrent instructions","members":[{"memberId":"c1","kind":"agent","relateId":"0199c0de-0000-7000-8000-000000000004","name":"Member one","instructions":null,"isManager":false}]},
                {"nodeId":"handoff","kind":"handoffBlock","name":"Handoff","position":{"x":1160,"y":80},"routingStrategy":"direct","instructions":"Handoff block instructions","members":[],"handoffInstructions":"Handoff instructions","enableReturnToPrevious":true,"autonomous":false,"autonomousTurnLimit":4,"continuationPrompt":"Continue"},
                {"nodeId":"group","kind":"groupChatBlock","name":"Group","position":{"x":1280,"y":80},"routingStrategy":"direct","instructions":"Group instructions","members":[],"maxRounds":5},
                {"nodeId":"magentic","kind":"magenticBlock","name":"Magentic","position":{"x":1400,"y":80},"routingStrategy":"direct","instructions":"Magentic instructions","members":[{"memberId":"m1","kind":"agent","relateId":"0199c0de-0000-7000-8000-000000000005","name":"Manager","instructions":"Plan","isManager":true},{"memberId":"m2","kind":"workflowAsAgent","relateId":"0199c0de-0000-7000-8000-000000000006","name":"Worker","instructions":null,"isManager":false}],"maxRounds":7,"maxStalls":2,"maxResets":1,"requirePlanSignoff":true}
              ],
              "edges": [
                {"edgeId":"edge-1","sourceNodeId":"input","targetNodeId":"agent","isFanInBarrier":false,"isSwitchDefault":false,"switchCaseOrder":0,"choiceId":null,"label":null,"conditions":[{"field":"contains","value":"approved","path":""}]},
                {"edgeId":"edge-2","sourceNodeId":"agent","targetNodeId":"workflow","isFanInBarrier":false,"isSwitchDefault":true,"switchCaseOrder":0,"choiceId":null,"label":"Else","conditions":[]},
                {"edgeId":"edge-3","sourceNodeId":"gate","targetNodeId":"output","isFanInBarrier":false,"isSwitchDefault":false,"switchCaseOrder":0,"choiceId":"approve","label":"Approve","conditions":[]},
                {"edgeId":"edge-4","sourceNodeId":"workflow","targetNodeId":"output","isFanInBarrier":true,"isSwitchDefault":false,"switchCaseOrder":0,"choiceId":null,"label":"Wait","conditions":[]}
              ]
            }
            """;
        var dto = JsonSerializer.Deserialize<AgentflowDefinitionDto>(json, JsonOptions)!;

        var roundTripped = AgentflowDefinitionMapper.ToDto(AgentflowDefinitionMapper.ToDefinition(dto));

        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(json),
                JsonNode.Parse(JsonSerializer.Serialize(roundTripped, JsonOptions))
            )
        );
    }

    [Fact]
    public void ToDefinition_OutputNode_StoresSummaryModelOnColumn()
    {
        var dto = new AgentflowDefinitionDto(
            [
                new AgentflowOutputNodeDto(
                    "output",
                    "Output",
                    new AgentflowPositionDto(1, 2),
                    AgentflowRoutingStrategy.Direct,
                    true,
                    Guid.Parse("0199c0de-0000-7000-8000-000000000003"),
                    "Instructions"
                ),
            ],
            []
        );

        var definition = AgentflowDefinitionMapper.ToDefinition(dto);

        Assert.Equal(
            Guid.Parse("0199c0de-0000-7000-8000-000000000003"),
            Assert.Single(definition.Nodes).SummaryModelProviderId
        );
        Assert.Equal("{\"enableSummary\":true}", Assert.Single(definition.Nodes).ConfigJson);
    }

    [Fact]
    public void ToDefinition_NodeWithoutOptions_StoresEmptyObject()
    {
        var dto = new AgentflowDefinitionDto(
            [
                new AgentflowAgentNodeDto(
                    "agent",
                    "Agent",
                    new AgentflowPositionDto(1, 2),
                    AgentflowRoutingStrategy.Direct,
                    null,
                    null
                ),
            ],
            []
        );

        var node = Assert.Single(AgentflowDefinitionMapper.ToDefinition(dto).Nodes);

        Assert.Equal("{}", node.ConfigJson);
        Assert.Equal(AgentflowRoutingStrategy.Direct, node.RoutingStrategy);
    }

    [Fact]
    public void Deserialize_UnknownKind_Throws()
    {
        const string json = """
            {"nodes":[{"nodeId":"unknown","kind":"unknown","name":"Unknown","position":{"x":0,"y":0},"routingStrategy":"direct"}],"edges":[]}
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentflowDefinitionDto>(json, JsonOptions));
    }

    [Fact]
    public void Deserialize_BlockMemberKindOutsideAgentAndWorkflowAsAgent_Throws()
    {
        const string json = """
            {"nodes":[{"nodeId":"team","kind":"concurrentBlock","name":"Team","position":{"x":0,"y":0},"routingStrategy":"direct","instructions":null,"members":[{"memberId":"invalid","kind":"input","relateId":"0199c0de-0000-7000-8000-000000000004","name":"Invalid member","instructions":null,"isManager":false}]}],"edges":[]}
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentflowDefinitionDto>(json, JsonOptions));
    }

    [Fact]
    public void Deserialize_BlockMemberWithoutKind_Throws()
    {
        const string json = """
            {"nodes":[{"nodeId":"team","kind":"concurrentBlock","name":"Team","position":{"x":0,"y":0},"routingStrategy":"direct","instructions":null,"members":[{"memberId":"member","relateId":"0199c0de-0000-7000-8000-000000000004","name":"Member","instructions":null,"isManager":false}]}],"edges":[]}
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentflowDefinitionDto>(json, JsonOptions));
    }

    [Fact]
    public void Deserialize_UnsupportedConditionField_Throws()
    {
        const string json = """
            {"nodes":[],"edges":[{"edgeId":"edge","sourceNodeId":"input","targetNodeId":"output","isFanInBarrier":false,"isSwitchDefault":false,"switchCaseOrder":0,"choiceId":null,"label":null,"conditions":[{"field":"always","value":"true","path":""}]}]}
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentflowDefinitionDto>(json, JsonOptions));
    }

    [Fact]
    public void Deserialize_NumericConditionField_Throws()
    {
        const string json = """
            {"nodes":[],"edges":[{"edgeId":"edge","sourceNodeId":"input","targetNodeId":"output","isFanInBarrier":false,"isSwitchDefault":false,"switchCaseOrder":0,"choiceId":null,"label":null,"conditions":[{"field":0,"value":"true","path":""}]}]}
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentflowDefinitionDto>(json, JsonOptions));
    }

    [Theory]
    [InlineData(AgentflowConditionField.Contains, "contains")]
    [InlineData(AgentflowConditionField.NotContains, "notContains")]
    [InlineData(AgentflowConditionField.Equals, "equals")]
    [InlineData(AgentflowConditionField.Author, "author")]
    [InlineData(AgentflowConditionField.Role, "role")]
    [InlineData(AgentflowConditionField.MinMessages, "minMessages")]
    [InlineData(AgentflowConditionField.Result, "result")]
    public void Serialize_ConditionField_UsesSupportedCamelCaseValue(
        AgentflowConditionField field,
        string expectedValue
    )
    {
        var json = JsonSerializer.Serialize(new AgentflowConditionRuleDto(field, "value", ""), JsonOptions);

        Assert.Contains($"\"field\":\"{expectedValue}\"", json);
    }

    [Theory]
    [InlineData("""{"value":"matches","path":""}""")]
    [InlineData("""{"field":"unknown","value":"matches","path":""}""")]
    [InlineData("""{"field":0,"value":"matches","path":""}""")]
    public void Deserialize_ConditionFieldMissingOrInvalid_RejectsRule(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentflowConditionRuleDto>(json, JsonOptions));
    }

    [Fact]
    public void ToDto_UnsupportedStoredConditionField_ThrowsInvalidParam()
    {
        var definition = AgentflowDefinitionMapper.FromEntities(
            [],
            [
                new AgentflowEdge
                {
                    EdgeId = "edge",
                    SourceNodeId = "input",
                    TargetNodeId = "output",
                    ConditionJson = AgentflowDefinitionJson.WriteConditions([
                        new AgentflowConditionRule("always", "true", ""),
                    ]),
                },
            ],
            []
        );

        var exception = Assert.Throws<AgwException>(() => AgentflowDefinitionMapper.ToDto(definition));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }
}
