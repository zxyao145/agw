using System.Text.Json;
using Agw.Agents.ExternalAgents;
using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Tests;

public class ExternalAgentDefaultsTests
{
    [Theory]
    [InlineData(ExternalAgentKind.ClaudeCode)]
    [InlineData(ExternalAgentKind.Codex)]
    [InlineData(ExternalAgentKind.Pi)]
    public void GetDefaultExtra_SupportedKind_ReturnsJsonObject(ExternalAgentKind kind)
    {
        var extra = ExternalAgentDefaults.GetDefaultExtra(kind);

        using var document = JsonDocument.Parse(extra);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData(" { } ")]
    public void ShouldUseDefaultExtra_MissingOrEmptyObject_ReturnsTrue(string? extra)
    {
        Assert.True(ExternalAgentDefaults.ShouldUseDefaultExtra(extra));
    }

    [Theory]
    [InlineData("{\"model\":\"custom\"}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public void ShouldUseDefaultExtra_CustomOrInvalidValue_ReturnsFalse(string extra)
    {
        Assert.False(ExternalAgentDefaults.ShouldUseDefaultExtra(extra));
    }
}
