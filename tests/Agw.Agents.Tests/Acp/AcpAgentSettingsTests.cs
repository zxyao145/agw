using System.Text.Json;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpAgentSettingsTests
{
    [Fact]
    public void Parse_RegistryExample_ReturnsSettings()
    {
        var settings = AcpAgentSettingsJson.Parse(
            """{"registry":{"agentId":"goose"},"configOptions":{"model":"claude-sonnet-4-5","stream":true}}"""
        );

        AcpAgentSettingsRules.Validate(settings);
        var registry = Assert.IsType<Agw.Agents.ExternalAgents.Acp.AcpRegistryReference>(settings.Registry);
        Assert.Equal("goose", registry.AgentId);
        Assert.Null(registry.Version);
        Assert.Equal("claude-sonnet-4-5", settings.ConfigOptions!["model"].Text);
        Assert.True(settings.ConfigOptions["stream"].Boolean);
    }

    [Theory]
    [InlineData("""{"command":"x","comand":"y"}""", "comand")]
    [InlineData("""{"registry":{"agentId":"goose","verison":"1.0.0"}}""", "verison")]
    public void Parse_UnknownKey_ThrowsInvalidParam(string extra, string key)
    {
        var error = Assert.Throws<AgwException>(() => AcpAgentSettingsJson.Parse(extra));

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Contains(key, error.Message);
    }

    [Fact]
    public void Validate_MissingLaunchSource_ThrowsInvalidParam()
    {
        var error = Assert.Throws<AgwException>(() => Validate("{}"));

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Equal("ACP agents require either registry or command in Extra Settings.", error.Message);
    }

    [Theory]
    [InlineData("""{"registry":{"agentId":"a"},"command":"b"}""", "command")]
    [InlineData("""{"command":"  "}""", "command")]
    [InlineData("""{"command":"x\ny"}""", "command")]
    [InlineData("""{"args":[],"registry":{"agentId":"a"}}""", "args")]
    [InlineData("""{"registry":{"agentId":"Goose"}}""", "registry.agentId")]
    [InlineData("""{"registry":{"agentId":"1abc"}}""", "registry.agentId")]
    [InlineData("""{"registry":{"agentId":"goose","version":"1.2"}}""", "registry.version")]
    [InlineData("""{"command":"x","configOptions":{"model":{"a":1}}}""", "model")]
    [InlineData("""{"command":"x","configOptions":{"model":null}}""", "model")]
    [InlineData("""{"command":"x","modeId":""}""", "modeId")]
    [InlineData("""{"command":"x","args":[null]}""", "args")]
    public void Validate_InvalidFields_ThrowsInvalidParam(string extra, string key)
    {
        var error = Assert.Throws<AgwException>(() => Validate(extra));

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Contains(key, error.Message);
    }

    [Fact]
    public void Validate_Limits_ThrowsInvalidParam()
    {
        var args = JsonSerializer.Serialize(new { command = "x", args = Enumerable.Repeat("a", 65) });
        var tooMany = JsonSerializer.Serialize(
            new
            {
                command = "x",
                configOptions = Enumerable.Range(1, 33).ToDictionary(number => $"option{number}", _ => "x"),
            }
        );
        var tooLong = JsonSerializer.Serialize(
            new { command = "x", configOptions = new { model = new string('a', 1001) } }
        );

        Assert.Contains("args", Assert.Throws<AgwException>(() => Validate(args)).Message);
        Assert.Contains("configOptions", Assert.Throws<AgwException>(() => Validate(tooMany)).Message);
        Assert.Contains("model", Assert.Throws<AgwException>(() => Validate(tooLong)).Message);
    }

    private static void Validate(string extra) => AcpAgentSettingsRules.Validate(AcpAgentSettingsJson.Parse(extra));
}
