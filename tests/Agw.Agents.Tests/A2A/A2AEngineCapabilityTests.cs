using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Execution.Agents.ExternalAgents;
using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Tests.A2A;

public sealed class A2AEngineCapabilityTests
{
    [Fact]
    public void Resolve_ExternalA2A_ReturnsA2A() =>
        Assert.Equal(EngineKind.A2A, EngineKinds.Resolve(AgentType.External, EngineKind.A2A));

    [Fact]
    public void ForAgent_A2A_AllowsFullAccessOnly()
    {
        var capabilities = ExecutionPermissionService.ForAgent(
            new Agent { Type = AgentType.External, ExternalAgentKind = EngineKind.A2A }
        );

        Assert.Equal([AgwPermissionMode.FullAccess], capabilities.SupportedPermissionModes);
        Assert.False(capabilities.SupportsNoTools);
        Assert.Equal("Remote A2A agents run their own tools; Agw permission modes do not apply.", capabilities.Reason);
    }

    [Theory]
    [InlineData(ExecutionProvider.InProcess)]
    [InlineData(ExecutionProvider.Distributed)]
    public void ForEngine_A2A_ReportsA2AReason(ExecutionProvider provider)
    {
        var capabilities = ExecutionInputCapabilities.ForEngine(EngineKind.A2A, provider);

        Assert.False(capabilities.SupportsSteer);
        Assert.Equal("A2A agents do not accept input while a turn is running.", capabilities.Reason);
    }
}
