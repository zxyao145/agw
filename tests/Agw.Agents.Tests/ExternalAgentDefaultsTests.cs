using System.Text.Json;
using Agw.Agents.ExternalAgents;

namespace Agw.Agents.Tests;

public class ExternalAgentDefaultsTests
{
    [Fact]
    public void GetDisplayName_Acp_ReturnsAcp() =>
        Assert.Equal("ACP", ExternalAgentDefaults.GetDisplayName(EngineKind.ACP));

    [Fact]
    public void GetDefaultExtra_A2A_ReturnsEmptyObject() =>
        Assert.Equal("{}", ExternalAgentDefaults.GetDefaultExtra(EngineKind.A2A));

    [Fact]
    public void GetSupportedProviderTypes_A2A_ReturnsEmpty() =>
        Assert.Empty(ExternalAgentDefaults.GetSupportedProviderTypes(EngineKind.A2A));

    [Theory]
    [InlineData(EngineKind.ClaudeCode)]
    [InlineData(EngineKind.Codex)]
    [InlineData(EngineKind.Pi)]
    public void GetDefaultExtra_SupportedKind_ReturnsJsonObject(EngineKind kind)
    {
        var extra = ExternalAgentDefaults.GetDefaultExtra(kind);

        using var document = JsonDocument.Parse(extra);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }
}
