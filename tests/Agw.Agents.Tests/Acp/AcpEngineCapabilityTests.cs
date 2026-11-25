using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Execution.Agents.ExternalAgents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpEngineCapabilityTests
{
    [Fact]
    public void ForAgent_AcpInProcess_AllowsAllPermissionModes()
    {
        var capabilities = ExecutionPermissionService.ForAgent(External(EngineKind.ACP), ExecutionProvider.InProcess);

        Assert.Equal(
            [AgwPermissionMode.FullAccess, AgwPermissionMode.AlwaysAsk, AgwPermissionMode.AllowSameArguments],
            capabilities.SupportedPermissionModes
        );
        Assert.False(capabilities.SupportsNoTools);
        Assert.Null(capabilities.Reason);
    }

    [Fact]
    public void ForAgent_AcpDistributed_AllowsFullAccessOnly()
    {
        var agent = External(EngineKind.ACP);
        var capabilities = ExecutionPermissionService.ForAgent(agent, ExecutionProvider.Distributed);

        Assert.Equal([AgwPermissionMode.FullAccess], capabilities.SupportedPermissionModes);
        Assert.False(capabilities.SupportsNoTools);
        Assert.Equal(
            "ACP agents support Full access only with the Distributed execution provider.",
            capabilities.Reason
        );
        var error = Assert.Throws<AgwException>(() =>
            ExecutionPermissionService.Validate(agent, ExecutionProvider.Distributed, AgwPermissionMode.AlwaysAsk)
        );
        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Fact]
    public void ForAgent_CodexDistributed_Unchanged()
    {
        var agent = External(EngineKind.Codex);
        var expected = ExecutionPermissionService.ForAgent(agent, ExecutionProvider.InProcess);
        var actual = ExecutionPermissionService.ForAgent(agent, ExecutionProvider.Distributed);

        Assert.Equal(expected.SupportedPermissionModes, actual.SupportedPermissionModes);
        Assert.Equal(expected.SupportsNoTools, actual.SupportsNoTools);
        Assert.Equal(expected.Reason, actual.Reason);
    }

    [Theory]
    [InlineData(ExecutionProvider.InProcess)]
    [InlineData(ExecutionProvider.Distributed)]
    public void ForEngine_Acp_ReportsAcpReason(ExecutionProvider provider)
    {
        var capabilities = ExecutionInputCapabilities.ForEngine(EngineKind.ACP, provider);

        Assert.False(capabilities.SupportsSteer);
        Assert.Equal("ACP agents do not accept input while a turn is running.", capabilities.Reason);
    }

    [Theory]
    [InlineData(EngineKind.Maf, true)]
    [InlineData(EngineKind.A2A, true)]
    [InlineData(EngineKind.ACP, true)]
    [InlineData(EngineKind.ClaudeCode, false)]
    [InlineData(EngineKind.Codex, false)]
    [InlineData(EngineKind.Pi, false)]
    public void SupportsDistributedExecution_Kinds_MatchDesign(EngineKind kind, bool supported) =>
        Assert.Equal(supported, EngineKinds.SupportsDistributedExecution(kind));

    private static Agent External(EngineKind kind) => new() { Type = AgentType.External, ExternalAgentKind = kind };
}
