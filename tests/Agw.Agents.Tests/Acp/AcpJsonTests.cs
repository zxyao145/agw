using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp.Protocol;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpJsonTests
{
    [Fact]
    public void Deserialize_UnknownSessionUpdate_ReturnsBaseType() =>
        Assert.IsType<AcpSessionUpdate>(
            JsonSerializer.Deserialize<AcpSessionUpdate>(
                """{"sessionUpdate":"cursor_update","cursorId":"c"}""",
                AcpJson.Options
            )
        );

    [Fact]
    public void Deserialize_DiscriminatorNotFirst_ParsesDerived()
    {
        var update = JsonSerializer.Deserialize<AcpSessionUpdate>(
            """{"content":{"text":"hi","type":"text"},"sessionUpdate":"agent_message_chunk"}""",
            AcpJson.Options
        );
        Assert.Equal(new AcpTextContent("hi"), Assert.IsType<AcpAgentMessageChunk>(update).Content);
    }

    [Theory]
    [InlineData("tool_call")]
    [InlineData("tool_call_update")]
    public void Deserialize_ToolCallsWithExplicitNull_DistinguishesOmittedRawFields(string kind)
    {
        var wire = new JsonObject
        {
            ["sessionUpdate"] = kind,
            ["toolCallId"] = "call",
            ["title"] = "Edit",
        };
        var omitted = wire.Deserialize<AcpSessionUpdate>(AcpJson.Options);
        Assert.True(JsonNode.DeepEquals(wire, JsonSerializer.SerializeToNode(omitted, AcpJson.Options)));
        wire["rawInput"] = null;
        wire["rawOutput"] = null;
        var present = wire.Deserialize<AcpSessionUpdate>(AcpJson.Options);
        Assert.True(JsonNode.DeepEquals(wire, JsonSerializer.SerializeToNode(present, AcpJson.Options)));
    }

    [Fact]
    public void Serialize_Initialize_MatchesDesign()
    {
        var json = JsonSerializer.SerializeToNode(AcpInitializeRequest.Create("1.0"), AcpJson.Options);
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(
                    """
                    {"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},
                    "terminal":false,"session":{"configOptions":{"boolean":{}}}},"clientInfo":{"name":"agw","title":"Agw","version":"1.0"}}
                    """
                ),
                json
            )
        );
    }
}
