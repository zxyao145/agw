using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Shared.Data.Entities.Agentflows;

namespace Agw.Agents.Tests;

public sealed class AgentflowDefinitionJsonTests
{
    [Fact]
    public void WriteOptions_HumanGate_UsesCamelCaseKeys()
    {
        var options = new HumanGateOptions("Review", [new AgentflowGateChoice("choice-1", "Approve")], true);

        var json = AgentflowDefinitionJson.WriteOptions(options);
        var parsed = AgentflowDefinitionJson.ReadOptions<HumanGateOptions>(new AgentflowNode { ConfigJson = json });

        Assert.Equal(
            "{\"humanPrompt\":\"Review\",\"choices\":[{\"choiceId\":\"choice-1\",\"label\":\"Approve\"}],\"allowResponseText\":true}",
            json
        );
        Assert.Equal(options.HumanPrompt, parsed.HumanPrompt);
        Assert.Equal(options.Choices, parsed.Choices);
        Assert.Equal(options.AllowResponseText, parsed.AllowResponseText);
    }

    [Fact]
    public void ReadConditions_EmptyArray_ReturnsNoRules()
    {
        var conditions = AgentflowDefinitionJson.ReadConditions(new AgentflowEdge { ConditionJson = "[]" });

        Assert.Empty(conditions);
    }

    [Fact]
    public void WriteConditions_ResultRule_KeepsPath()
    {
        var rules = new[] { new AgentflowConditionRule("result", "approved", "review.status") };

        var json = AgentflowDefinitionJson.WriteConditions(rules);
        var parsed = AgentflowDefinitionJson.ReadConditions(new AgentflowEdge { ConditionJson = json });

        Assert.Equal(rules, parsed);
    }
}
