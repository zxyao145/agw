using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.Outbound;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class ExecutionScopeTests
{
    [Fact]
    public void Required_WithoutScope_ThrowsExecutionContextMissing()
    {
        var accessor = new AgentExecutionContextAccessor();

        Assert.Null(accessor.Current);
        var exception = Assert.Throws<AgwException>(() => accessor.Required);
        Assert.Equal(ErrorCodes.ExecutionContextMissing.Code, exception.Code);
        Assert.Null(UserInfoUtil.UserId);
    }

    [Fact]
    public async Task Push_WithoutUser_EstablishesIdentityAndOwnerFilter()
    {
        using var database = new TestAgentDatabase();
        using (UserInfoUtil.PushSystemScope())
        {
            database.Context.Agents.AddRange(
                new Agent
                {
                    Id = Guid.CreateVersion7(),
                    Name = "owned",
                    CreateBy = "owner",
                },
                new Agent
                {
                    Id = Guid.CreateVersion7(),
                    Name = "foreign",
                    CreateBy = "another",
                }
            );
            await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        database.Context.ChangeTracker.Clear();
        var scope = ExecutionTestScopes.Scope(ExecutionTestScopes.Context(userId: "owner"));

        using (scope.Push())
        {
            Assert.Equal("owner", UserInfoUtil.RequiredUserId);
            Assert.Same(scope, ExecutionContextSlot.Current);
            var visible = await database
                .Context.Agents.AsNoTracking()
                .Select(agent => agent.Name)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["owned"], visible);
        }

        Assert.Null(UserInfoUtil.Current);
        Assert.False(UserInfoUtil.IsContextActive);
        Assert.Null(ExecutionContextSlot.Current);
    }

    [Fact]
    public void Push_WithAnotherUser_ThrowsExecutionOwnerMismatch()
    {
        var scope = ExecutionTestScopes.Scope(ExecutionTestScopes.Context(userId: "owner"));
        using var user = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "another")], "test"))
        );

        var exception = Assert.Throws<AgwException>(() => scope.Push());

        Assert.Equal(ErrorCodes.ExecutionOwnerMismatch.Code, exception.Code);
        Assert.Null(ExecutionContextSlot.Current);
        Assert.Equal("another", UserInfoUtil.UserId);
    }

    [Fact]
    public void Create_WithBlankUser_ThrowsAuthenticationRequired()
    {
        var exception = Assert.Throws<AgwException>(() =>
            ExecutionTestScopes.Scope(ExecutionTestScopes.Context(userId: " "))
        );

        Assert.Equal(ErrorCodes.AuthenticationRequired.Code, exception.Code);
    }

    [Fact]
    public void CreateNodeScope_FromTurnScope_ChangesExecutorAndKeepsTurnData()
    {
        var parent = ExecutionTestScopes.Scope(
            ExecutionTestScopes.Context(
                runtimeType: AgentRuntimeType.Agentflow,
                permissionMode: AgwPermissionMode.AlwaysAsk,
                permissionVersion: 3
            )
        );
        var node = new AgentflowNodeExecution(parent.Context.TurnTargetId, "writer", "Writer", 2, "writer_UserInput");
        var nodeAgentId = Guid.CreateVersion7();

        var child = parent.CreateNodeScope(node, nodeAgentId, EngineKind.Pi);

        Assert.Same(parent, child.Parent);
        Assert.Same(node, child.Context.Node);
        Assert.Equal(nodeAgentId, child.Context.AgentId);
        Assert.Equal(EngineKind.Pi, child.Context.EngineKind);
        Assert.Equal(
            parent.Context with
            {
                AgentId = nodeAgentId,
                EngineKind = EngineKind.Pi,
                Node = node,
            },
            child.Context
        );
        Assert.Equal(AgentRuntimeType.Agentflow, child.Context.RuntimeType);
        Assert.Equal(parent.Context.TurnTargetId, child.Context.TurnTargetId);
        Assert.Equal(AgwPermissionMode.AlwaysAsk, child.Permissions.Current);
        Assert.Equal(3, child.Permissions.Snapshot.Version);
    }

    [Fact]
    public void CreateNodeScope_TwoNodes_IsolateStepsAndShareTurnServices()
    {
        var output = new RecordingSink();
        var parent = ExecutionTestScopes.Scope(output: output);
        var first = parent.CreateNodeScope(
            new AgentflowNodeExecution(Guid.CreateVersion7(), "first", null, 0, "first_UserInput"),
            Guid.CreateVersion7(),
            EngineKind.Maf
        );
        var second = parent.CreateNodeScope(
            new AgentflowNodeExecution(Guid.CreateVersion7(), "second", null, 0, "second_UserInput"),
            Guid.CreateVersion7(),
            EngineKind.Maf
        );
        var firstOptions = ExecutionRunOptions.With(null, first.Context);
        var secondOptions = ExecutionRunOptions.With(null, second.Context);

        first.AdvanceStep();
        first.AdvanceStep();
        second.AdvanceStep();

        Assert.Same(first.Context, ExecutionRunOptions.Read(firstOptions));
        Assert.Same(second.Context, ExecutionRunOptions.Read(secondOptions));
        Assert.Equal(2, first.StepIndex);
        Assert.Equal(1, second.StepIndex);
        Assert.Equal(0, parent.StepIndex);
        Assert.Same(output, first.Output);
        Assert.Same(output, second.Output);
        Assert.Same(parent.Interactions, first.Interactions);
        using (parent.Push())
        {
            using (first.Push())
            {
                Assert.Same(first, ExecutionScope.Current);
            }

            Assert.Same(parent, ExecutionScope.Current);
        }
    }

    [Fact]
    public void CreateBackgroundAgentScope_FromTurnScope_RemovesInteractionChannel()
    {
        var parent = ExecutionTestScopes.Scope();
        var channel = new ResolvedHumanInteractionChannelProbe();
        parent.BindInteractions(handler: null, channel, requests: null);
        var backgroundAgentId = Guid.CreateVersion7();

        var background = parent.CreateBackgroundAgentScope(backgroundAgentId, EngineKind.Codex);

        Assert.Same(channel, parent.InteractionChannel);
        Assert.Null(background.InteractionChannel);
        Assert.Equal(backgroundAgentId, background.Context.AgentId);
        Assert.Equal(EngineKind.Codex, background.Context.EngineKind);
        Assert.Equal(parent.Context.TurnId, background.Context.TurnId);
    }

    [Fact]
    public void BindInteractions_Twice_Throws()
    {
        var scope = ExecutionTestScopes.Scope();
        scope.BindInteractions(handler: null, channel: null, requests: null);

        var exception = Assert.Throws<AgwException>(() =>
            scope.BindInteractions(handler: null, channel: null, requests: null)
        );

        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
    }

    [Fact]
    public void Current_HostScope_ReadsScopeContextUntilDisposed()
    {
        var accessor = new AgentExecutionContextAccessor();
        var scope = ExecutionTestScopes.Scope();

        using (scope.Push())
        {
            Assert.Same(scope.Context, accessor.Current);
            Assert.Same(scope.Context, accessor.Required);
            Assert.Equal(scope.Context.WorkspaceSnapshot, ExecutionContextSlot.GetWorkspaceSnapshot(scope.ProjectId));
            Assert.Null(ExecutionContextSlot.GetWorkspaceSnapshot(Guid.CreateVersion7()));
            Assert.Same(scope, ExecutionContextSlot.FindBound(scope.ProjectId, scope.ContextId.ToUpperInvariant()));
            Assert.Null(ExecutionContextSlot.FindBound(scope.ProjectId, "another"));
        }

        Assert.Null(accessor.Current);
        Assert.Throws<AgwException>(() => accessor.Required);
    }

    [Fact]
    public async Task Current_InsideAgentRun_ReadsInjectedRunOptions()
    {
        var accessor = new AgentExecutionContextAccessor();
        var turn = ExecutionTestScopes.Scope();
        var node = turn.CreateNodeScope(
            new AgentflowNodeExecution(Guid.CreateVersion7(), "node", null, 0, "node_UserInput"),
            Guid.CreateVersion7(),
            EngineKind.Maf
        );
        var agent = new ContextProbeAgent(accessor);

        using (turn.Push())
        {
            await agent.RunAsync(
                [new ChatMessage(ChatRole.User, "hello")],
                options: ExecutionRunOptions.With(null, node.Context),
                cancellationToken: TestContext.Current.CancellationToken
            );
        }

        Assert.Same(node.Context, agent.FromRunContext);
        Assert.Same(node.Context, agent.FromAccessor);
    }

    [Fact]
    public async Task Required_InsideAgentRunWithoutInjectedOptions_ThrowsExecutionContextMissing()
    {
        var accessor = new AgentExecutionContextAccessor();
        var agent = new ContextProbeAgent(accessor);

        using (ExecutionTestScopes.Scope().Push())
        {
            await agent.RunAsync(
                [new ChatMessage(ChatRole.User, "hello")],
                cancellationToken: TestContext.Current.CancellationToken
            );
        }

        Assert.Null(agent.FromAccessor);
        Assert.Equal(ErrorCodes.ExecutionContextMissing.Code, agent.RequiredFailure?.Code);
    }

    [Fact]
    public async Task RunStreaming_ConsumerOutsideScope_ReestablishesScopeOnEveryStep()
    {
        var scope = ExecutionTestScopes.Scope();
        var observed = new List<IExecutionIdentity?>();

        await foreach (
            var identity in scope
                .RunStreaming(ReadSlotAsync(TestContext.Current.CancellationToken))
                .WithCancellation(TestContext.Current.CancellationToken)
        )
        {
            observed.Add(identity);
            Assert.Null(ExecutionContextSlot.Current);
        }

        Assert.Equal([scope, scope, scope], observed);
        Assert.Null(ExecutionContextSlot.Current);
        Assert.Null(UserInfoUtil.Current);
    }

    [Fact]
    public void Source_NodeScope_ProjectsNodeIdentity()
    {
        var accessor = new HumanInteractionContextAccessor(new AgentExecutionContextAccessor());
        var turn = ExecutionTestScopes.Scope();
        var node = turn.CreateNodeScope(
            new AgentflowNodeExecution(Guid.CreateVersion7(), "parent/child", "Child", 0, "child_UserInput"),
            Guid.CreateVersion7(),
            EngineKind.Maf
        );

        using (turn.Push())
        {
            Assert.Equal("standalone", accessor.Source.NodeId);
            using (node.Push())
            {
                Assert.Equal(
                    new InteractionSource
                    {
                        NodeId = "parent/child",
                        NodeName = "Child",
                        ProviderScopeId = "child_UserInput",
                    },
                    accessor.Source
                );
            }
        }
    }

    [Fact]
    public void WithoutExecution_ChatOptionsCarryContext_RemovesOnlyExecutionKey()
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ExecutionRunOptions.ExecutionKey] = ExecutionTestScopes.Context(),
                ["kept"] = "value",
            },
        };

        var filtered = ExecutionRunOptions.WithoutExecution(options);

        Assert.NotSame(options, filtered);
        Assert.False(filtered!.AdditionalProperties!.ContainsKey(ExecutionRunOptions.ExecutionKey));
        Assert.Equal("value", filtered.AdditionalProperties["kept"]);
        Assert.True(options.AdditionalProperties.ContainsKey(ExecutionRunOptions.ExecutionKey));
    }

    private static async IAsyncEnumerable<IExecutionIdentity?> ReadSlotAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        for (var index = 0; index < 3; index++)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return ExecutionContextSlot.Current;
        }
    }

    private sealed class ContextProbeAgent : AIAgent
    {
        private readonly AgentExecutionContextAccessor _accessor;

        public ContextProbeAgent(AgentExecutionContextAccessor accessor)
        {
            _accessor = accessor;
        }

        public AgentExecutionContext? FromRunContext { get; private set; }

        public AgentExecutionContext? FromAccessor { get; private set; }

        public AgwException? RequiredFailure { get; private set; }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            FromRunContext = ExecutionRunOptions.Read(CurrentRunContext?.RunOptions);
            FromAccessor = _accessor.Current;
            try
            {
                _ = _accessor.Required;
            }
            catch (AgwException exception)
            {
                RequiredFailure = exception;
            }
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class ResolvedHumanInteractionChannelProbe : IHumanInteractionChannel
    {
        public ValueTask<UserInputResponse> RequestAsync(
            UserInputRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class RecordingSink : IExecutionMessageSink
    {
        public List<AgwMessage> Messages { get; } = [];

        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return ValueTask.CompletedTask;
        }
    }
}
