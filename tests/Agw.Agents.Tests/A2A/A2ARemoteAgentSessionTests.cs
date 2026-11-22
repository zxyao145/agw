using global::A2A;
using Agw.Agents.Execution.Agents.ExternalAgents;
using Agw.Agents.Execution.Agents.ExternalAgents.A2A;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests.A2A;

public sealed class A2ARemoteAgentSessionTests
{
    [Fact]
    public void Serialize_SessionWithTask_RoundTrips()
    {
        var state = CreateSession();
        state.StateBag.SetValue("test", "state");

        var json = state.Serialize(null);
        var restored = A2ARemoteAgentSession.Deserialize(json, null);

        Assert.Equal(state.InterfaceRevision, json.GetProperty("interfaceRevision").GetGuid());
        Assert.Equal("input-required", json.GetProperty("taskState").GetString());
        Assert.Equal(state.InterfaceRevision, restored.InterfaceRevision);
        Assert.Equal("c-01", restored.ContextId);
        Assert.Equal("t-07", restored.TaskId);
        Assert.Equal(TaskState.InputRequired, restored.TaskState);
        Assert.True(restored.StateBag.TryGetValue<string>("test", out var value));
        Assert.Equal("state", value);
    }

    [Fact]
    public void RecordContext_DifferentValue_ThrowsRequestFailed() =>
        Assert.Equal(
            ErrorCodes.A2ARemoteAgentRequestFailed.Code,
            Assert.Throws<AgwException>(() => CreateSession().RecordContext("other")).Code
        );

    [Fact]
    public void ClearTask_AfterTask_ResetsTaskOnly()
    {
        var state = CreateSession();
        state.ClearTask();

        Assert.Equal("c-01", state.ContextId);
        Assert.Null(state.TaskId);
        Assert.Null(state.TaskState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BindEndpointRevision_ChangedRevision_ClearsRemoteState(bool change)
    {
        var state = CreateSession();
        var revision = change ? Guid.NewGuid() : state.InterfaceRevision!.Value;

        state.BindEndpointRevision(revision);

        Assert.Equal(revision, state.InterfaceRevision);
        Assert.Equal(change ? null : "c-01", state.ContextId);
        Assert.Equal(change ? null : "t-07", state.TaskId);
        Assert.Equal(change ? null : TaskState.InputRequired, state.TaskState);
    }

    [Fact]
    public void ClearRemoteState_AfterTaskNotFound_KeepsRevision()
    {
        var state = CreateSession();
        var revision = state.InterfaceRevision;
        state.ClearRemoteState();

        Assert.Equal(revision, state.InterfaceRevision);
        Assert.Null(state.ContextId);
        Assert.Null(state.TaskId);
        Assert.Null(state.TaskState);
    }

    [Theory]
    [InlineData(EngineKind.Maf, true)]
    [InlineData(EngineKind.A2A, true)]
    [InlineData(EngineKind.Codex, false)]
    [InlineData(EngineKind.ClaudeCode, false)]
    [InlineData(EngineKind.Pi, false)]
    public void UsesSdkSession_PerEngine_ReturnsExpected(EngineKind engine, bool expected) =>
        Assert.Equal(expected, EngineKinds.UsesSdkSession(engine));

    [Fact]
    public void UsesProviderSessionBinding_A2A_ReturnsFalse() =>
        Assert.False(
            ExternalProviderSessionBindings.UsesProviderSessionBinding(
                new Agent { Type = AgentType.External, ExternalAgentKind = EngineKind.A2A }
            )
        );

    private static A2ARemoteAgentSession CreateSession()
    {
        var state = new A2ARemoteAgentSession();
        state.BindEndpointRevision(Guid.NewGuid());
        state.RecordContext("c-01");
        state.RecordTask("t-07", TaskState.InputRequired);
        return state;
    }
}
