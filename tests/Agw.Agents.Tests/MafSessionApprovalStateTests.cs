using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public class MafSessionApprovalStateTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(PermissionMode.AlwaysAsk)]
    [InlineData(PermissionMode.AllowSameArguments)]
    [InlineData(PermissionMode.FullAccess)]
    public void Record_Once_DoesNotGrantLaterCalls(PermissionMode? mode)
    {
        var session = new TestSession();
        var call = CreateCall("run_shell", "{\"command\":\"pwd\"}");

        MafSessionApprovalState.Record(session, call, ApprovalScope.Once, mode);

        Assert.False(MafSessionApprovalState.TryApprove(CreateContext(session, call), mode));
    }

    [Theory]
    [InlineData(ApprovalScope.AlwaysTool)]
    [InlineData(ApprovalScope.AlwaysArguments)]
    public void Record_AlwaysAsk_DoesNotGrantLaterCalls(ApprovalScope scope)
    {
        var session = new TestSession();
        var call = CreateCall("run_shell", "{\"command\":\"pwd\"}");

        MafSessionApprovalState.Record(session, call, scope, PermissionMode.AlwaysAsk);

        Assert.False(MafSessionApprovalState.TryApprove(CreateContext(session, call), PermissionMode.AlwaysAsk));
    }

    [Fact]
    public void TryApprove_AlwaysTool_MatchesOnlyOrdinalToolName()
    {
        var session = new TestSession();
        MafSessionApprovalState.Record(session, CreateCall("run_shell", "{}"), ApprovalScope.AlwaysTool, null);

        var sameTool = MafSessionApprovalState.TryApprove(
            CreateContext(session, CreateCall("run_shell", "{\"command\":\"pwd\"}")),
            null
        );
        var differentCase = MafSessionApprovalState.TryApprove(
            CreateContext(session, CreateCall("RUN_SHELL", "{}")),
            null
        );
        var differentTool = MafSessionApprovalState.TryApprove(
            CreateContext(session, CreateCall("read_file", "{}")),
            null
        );

        Assert.True(sameTool);
        Assert.False(differentCase);
        Assert.False(differentTool);
    }

    [Theory]
    [InlineData("{\"options\":{\"flags\":[\"a\",\"b\"],\"limit\":1.0},\"command\":\"pwd\"}", true)]
    [InlineData("{\"command\":\"pwd\",\"options\":{\"limit\":1e0,\"flags\":[\"a\",\"b\"]}}", true)]
    [InlineData("{\"command\":\"pwd\",\"options\":{\"limit\":\"1\",\"flags\":[\"a\",\"b\"]}}", false)]
    [InlineData("{\"command\":\"pwd\",\"options\":{\"limit\":1,\"flags\":[\"b\",\"a\"]}}", false)]
    [InlineData("{\"Command\":\"pwd\",\"options\":{\"limit\":1,\"flags\":[\"a\",\"b\"]}}", false)]
    public void TryApprove_AlwaysArguments_UsesJsonSemantics(string candidateArguments, bool expected)
    {
        var session = new TestSession();
        var call = new FunctionCallContent(
            "approved",
            "run_shell",
            new Dictionary<string, object?>
            {
                ["command"] = "pwd",
                ["options"] = new Dictionary<string, object?> { ["limit"] = 1, ["flags"] = new[] { "a", "b" } },
            }
        );
        MafSessionApprovalState.Record(session, call, ApprovalScope.AlwaysArguments, null);

        var approved = MafSessionApprovalState.TryApprove(
            CreateContext(session, CreateCall("run_shell", candidateArguments)),
            null
        );

        Assert.Equal(expected, approved);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("{}", false)]
    [InlineData("{\"value\":null}", false)]
    public void TryApprove_NullArguments_MatchesOnlyNullArguments(string? candidateArguments, bool expected)
    {
        var session = new TestSession();
        MafSessionApprovalState.Record(session, CreateCall("run_shell", null), ApprovalScope.AlwaysArguments, null);

        var approved = MafSessionApprovalState.TryApprove(
            CreateContext(session, CreateCall("run_shell", candidateArguments)),
            null
        );

        Assert.Equal(expected, approved);
    }

    [Fact]
    public void Record_ArgumentsLaterMutated_KeepsApprovedSnapshot()
    {
        var session = new TestSession();
        var options = new JsonObject { ["limit"] = 1 };
        var call = new FunctionCallContent(
            "approved",
            "run_shell",
            new Dictionary<string, object?> { ["options"] = options }
        );
        MafSessionApprovalState.Record(session, call, ApprovalScope.AlwaysArguments, null);

        options["limit"] = 2;

        Assert.True(
            MafSessionApprovalState.TryApprove(
                CreateContext(session, CreateCall("run_shell", "{\"options\":{\"limit\":1}}")),
                null
            )
        );
        Assert.False(MafSessionApprovalState.TryApprove(CreateContext(session, call), null));
    }

    [Theory]
    [InlineData(ApprovalScope.AlwaysTool)]
    [InlineData(ApprovalScope.AlwaysArguments)]
    public void Record_AllowSameArguments_RestrictsStandingGrantToApprovedArguments(ApprovalScope scope)
    {
        var session = new TestSession();
        var call = CreateCall("run_shell", "{\"command\":\"pwd\"}");

        MafSessionApprovalState.Record(session, call, scope, PermissionMode.AllowSameArguments);

        Assert.True(
            MafSessionApprovalState.TryApprove(CreateContext(session, call), PermissionMode.AllowSameArguments)
        );
        Assert.False(
            MafSessionApprovalState.TryApprove(
                CreateContext(session, CreateCall("run_shell", "{\"command\":\"ls\"}")),
                PermissionMode.AllowSameArguments
            )
        );
    }

    [Theory]
    [InlineData(null, PermissionMode.AlwaysAsk)]
    [InlineData(null, PermissionMode.AllowSameArguments)]
    [InlineData(null, PermissionMode.FullAccess)]
    [InlineData(PermissionMode.AllowSameArguments, null)]
    [InlineData(PermissionMode.FullAccess, PermissionMode.AllowSameArguments)]
    public void Apply_ModeChanges_RevokesGrantsWithoutRestoringThemOnReturn(
        PermissionMode? originalMode,
        PermissionMode? nextMode
    )
    {
        var session = new TestSession();
        var call = CreateCall("run_shell", "{}");
        MafSessionApprovalState.Record(session, call, ApprovalScope.AlwaysArguments, originalMode);

        MafSessionApprovalState.Apply(session, nextMode);
        var afterChange = MafSessionApprovalState.TryApprove(CreateContext(session, call), nextMode);
        MafSessionApprovalState.Apply(session, originalMode);
        var afterReturn = MafSessionApprovalState.TryApprove(CreateContext(session, call), originalMode);

        Assert.False(afterChange);
        Assert.False(afterReturn);
    }

    [Fact]
    public void Apply_SameMode_PreservesGrants()
    {
        var session = new TestSession();
        var call = CreateCall("run_shell", "{}");
        MafSessionApprovalState.Record(session, call, ApprovalScope.AlwaysArguments, PermissionMode.AllowSameArguments);

        MafSessionApprovalState.Apply(session, PermissionMode.AllowSameArguments);

        Assert.True(
            MafSessionApprovalState.TryApprove(CreateContext(session, call), PermissionMode.AllowSameArguments)
        );
    }

    [Fact]
    public void TryApprove_ModeChangedSinceRegistration_RevokesGrants()
    {
        var session = new TestSession();
        var call = CreateCall("run_shell", "{}");
        MafSessionApprovalState.Record(session, call, ApprovalScope.AlwaysTool, null);

        var approved = MafSessionApprovalState.TryApprove(
            CreateContext(session, call),
            PermissionMode.AllowSameArguments
        );

        Assert.False(approved);
        Assert.False(MafSessionApprovalState.TryApprove(CreateContext(session, call), null));
    }

    [Fact]
    public void Record_ModeChanged_RevokesEarlierGrantsBeforeRecordingNewGrant()
    {
        var session = new TestSession();
        MafSessionApprovalState.Record(session, CreateCall("old_tool", "{}"), ApprovalScope.AlwaysTool, null);

        MafSessionApprovalState.Record(
            session,
            CreateCall("new_tool", "{}"),
            ApprovalScope.AlwaysArguments,
            PermissionMode.AllowSameArguments
        );

        Assert.False(
            MafSessionApprovalState.TryApprove(
                CreateContext(session, CreateCall("old_tool", "{}")),
                PermissionMode.AllowSameArguments
            )
        );
        Assert.True(
            MafSessionApprovalState.TryApprove(
                CreateContext(session, CreateCall("new_tool", "{}")),
                PermissionMode.AllowSameArguments
            )
        );
    }

    [Fact]
    public void TryApprove_FullAccessWithoutGrant_LeavesAutomaticApprovalToCaller()
    {
        var context = CreateContext(new TestSession(), CreateCall("run_shell", "{}"));

        var approved = MafSessionApprovalState.TryApprove(context, PermissionMode.FullAccess);

        Assert.False(approved);
    }

    [Fact]
    public void TryApprove_NoSession_DoesNotApprove()
    {
        var context = CreateContext(null, CreateCall("run_shell", "{}"));

        var approved = MafSessionApprovalState.TryApprove(context, null);

        Assert.False(approved);
    }

    [Fact]
    public void Grants_SessionRoundTrip_PreservesArgumentSemanticsAndSessionIsolation()
    {
        var original = new TestSession();
        MafSessionApprovalState.Record(
            original,
            CreateCall("run_shell", "{\"a\":1,\"b\":2}"),
            ApprovalScope.AlwaysArguments,
            null
        );

        var restored = new TestSession(AgentSessionStateBag.Deserialize(original.StateBag.Serialize()));
        var call = CreateCall("run_shell", "{\"b\":2.0,\"a\":1}");

        Assert.True(MafSessionApprovalState.TryApprove(CreateContext(restored, call), null));
        Assert.False(
            MafSessionApprovalState.TryApprove(
                CreateContext(restored, CreateCall("run_shell", "{\"A\":1,\"b\":2}")),
                null
            )
        );
        Assert.False(MafSessionApprovalState.TryApprove(CreateContext(new TestSession(), call), null));
        MafSessionApprovalState.Apply(restored, PermissionMode.AlwaysAsk);
        Assert.True(MafSessionApprovalState.TryApprove(CreateContext(original, call), null));
    }

    [Fact]
    public void Grants_ExistingSdkApprovalState_PreservesAllSdkFields()
    {
        using var snapshot = JsonDocument.Parse(
            """
            {"toolApprovalState":{
                "rules":[{"toolName":"legacy_tool"}],
                "collectedApprovalResponses":[{"requestId":"answered"}],
                "queuedApprovalRequests":[{"requestId":"queued"}],
                "surfacedApprovalRequests":{"pending":{"requestId":"pending"}}
            }}
            """
        );
        var session = new TestSession(AgentSessionStateBag.Deserialize(snapshot.RootElement));
        var call = CreateCall("run_shell", "{}");

        MafSessionApprovalState.Apply(session, null);
        MafSessionApprovalState.Record(session, call, ApprovalScope.AlwaysTool, null);
        Assert.True(MafSessionApprovalState.TryApprove(CreateContext(session, call), null));
        MafSessionApprovalState.Apply(session, PermissionMode.AlwaysAsk);
        MafSessionApprovalState.Record(session, call, ApprovalScope.Once, PermissionMode.AlwaysAsk);
        MafSessionApprovalState.TryApprove(CreateContext(session, call), PermissionMode.FullAccess);

        Assert.True(
            JsonElement.DeepEquals(
                snapshot.RootElement.GetProperty("toolApprovalState"),
                session.StateBag.Serialize().GetProperty("toolApprovalState")
            )
        );
    }

    [Fact]
    public async Task Set_SharedPermissions_DoesNotModifySessionUntilExecutionRegistersIt()
    {
        var permissions = new InteractionPermissionState(null);
        var state = new MafPermissionState(permissions);
        var session = new TestSession();
        var call = CreateCall("run_shell", "{}");
        state.Register(session);
        MafSessionApprovalState.Record(session, call, ApprovalScope.AlwaysTool, state.Current);
        var before = session.StateBag.Serialize();

        await Task.Run(() => state.Set(PermissionMode.AlwaysAsk), TestContext.Current.CancellationToken);

        Assert.Same(permissions, state.Permissions);
        Assert.Equal(PermissionMode.AlwaysAsk, permissions.Current);
        Assert.Equal(permissions.Current, state.Current);
        Assert.True(JsonElement.DeepEquals(before, session.StateBag.Serialize()));
        state.Register(session);
        permissions.Set(null);
        Assert.Null(state.Current);
        Assert.False(MafSessionApprovalState.TryApprove(CreateContext(session, call), state.Current));
    }

    private static FunctionCallContent CreateCall(string toolName, string? arguments) =>
        new(
            "call",
            toolName,
            arguments is null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments)
        );

    private static ToolAutoApprovalRuleContext CreateContext(AgentSession? session, FunctionCallContent call) =>
        new(call, new TestAgent(), session, [], null);

    private sealed class TestSession : AgentSession
    {
        public TestSession() { }

        public TestSession(AgentSessionStateBag stateBag)
            : base(stateBag) { }
    }

    private sealed class TestAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }
}
