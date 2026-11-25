using Agw.Agents.Execution.Agents.Runtime;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpProviderSessionTests
{
    [Theory]
    [InlineData(EngineKind.ACP, "sess_01JABC", "sess_01JABC")]
    [InlineData(EngineKind.Pi, "sess_01JABC", null)]
    [InlineData(EngineKind.Codex, "22222222-3333-4444-5555-666666666666", "22222222-3333-4444-5555-666666666666")]
    public void ResolveProviderSessionId_EngineIdFormat_PreservesSupportedValue(
        EngineKind kind,
        string stored,
        string? expected
    )
    {
        var agent = External(kind);

        Assert.Equal(expected, Resolve(agent, stored));
    }

    [Fact]
    public void ResolveProviderSessionId_AcpTooLong_ReturnsNull() =>
        Assert.Null(Resolve(External(EngineKind.ACP), new string('x', 201)));

    [Theory]
    [InlineData("sess_01JABC", true)]
    [InlineData(null, false)]
    public void ResolveExternalProviderSession_Acp_UsesPersistedValue(string? id, bool resume) =>
        Assert.Equal(
            (id, resume),
            ExternalProviderSessionBindings.ResolveExternalProviderSession(
                External(EngineKind.ACP),
                id,
                requestedResume: !resume
            )
        );

    [Fact]
    public void ResolveExternalProviderSession_ClaudeWithoutValue_GeneratesGuidString()
    {
        var (sessionId, resume) = ExternalProviderSessionBindings.ResolveExternalProviderSession(
            External(EngineKind.ClaudeCode),
            null,
            requestedResume: true
        );

        Assert.True(Guid.TryParse(sessionId, out _));
        Assert.False(resume);
    }

    private static string? Resolve(Agent agent, string id)
    {
        var bindings = new ExternalProviderSessionBindings(null!, NullLogger<ExternalProviderSessionBindings>.Instance);
        return bindings.ResolveProviderSessionId(
            agent,
            new ExternalProviderSessionState(
                new ProjectProviderSessionReference(Guid.NewGuid(), "context", agent.Id, agent.Name, 1),
                id
            )
        );
    }

    private static Agent External(EngineKind kind) => new() { Type = AgentType.External, ExternalAgentKind = kind };
}
