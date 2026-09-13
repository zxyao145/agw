using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using ClaudeCodeSdk.Types;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public class ClaudeCodeAskUserQuestionBridgeTests
{
    [Fact]
    public async Task HandleAsync_WithUserAnswers_EmitsInteractionAndReturnsUpdatedInput()
    {
        // Arrange
        var accessor = new HumanInteractionContextAccessor();
        var channel = new TestHumanInteractionChannel(request => new UserInputResponse
        {
            InteractionId = "test-interaction",
            Cancelled = false,
            ResponseData = JsonSerializer.SerializeToElement(
                new { answers = new Dictionary<string, string> { ["Continue?"] = "Yes" } }
            ),
        });
        var bridge = new ClaudeCodeAskUserQuestionBridge(accessor, allowInteraction: true);
        PermissionResult? permissionResult = null;
        var innerAgent = new CallbackAgent(async cancellationToken =>
        {
            permissionResult = await bridge.HandleAsync(
                "AskUserQuestion",
                CreateQuestionInput(),
                new ToolPermissionContext("call-1"),
                cancellationToken
            );
        });

        // Act
        using (accessor.Push(channel))
        {
            await bridge.BindRunAsync(
                [],
                session: null,
                options: null,
                innerAgent,
                TestContext.Current.CancellationToken
            );
        }

        // Assert
        var request = Assert.Single(channel.Requests);
        Assert.Equal("questions", request.InputKind);
        Assert.Equal("AskUserQuestion", request.Source.ToolName);
        Assert.Equal("call-1", request.Source.CallId);
        Assert.Equal("Continue?", request.Payload.GetProperty("questions")[0].GetProperty("question").GetString());
        var allow = Assert.IsType<PermissionResultAllow>(permissionResult);
        Assert.True(allow.UpdatedInput.HasValue);
        Assert.Equal("Yes", allow.UpdatedInput.Value.GetProperty("answers").GetProperty("Continue?").GetString());
        Assert.Equal(
            "Continue?",
            allow.UpdatedInput.Value.GetProperty("questions")[0].GetProperty("question").GetString()
        );
    }

    [Fact]
    public async Task BindRunAsync_AcrossSequentialRuns_UsesCurrentChannel()
    {
        // Arrange
        var accessor = new HumanInteractionContextAccessor();
        var firstChannel = CreateAnsweringChannel("First");
        var secondChannel = CreateAnsweringChannel("Second");
        var bridge = new ClaudeCodeAskUserQuestionBridge(accessor, allowInteraction: true);
        var innerAgent = new CallbackAgent(cancellationToken =>
            bridge
                .HandleAsync(
                    "AskUserQuestion",
                    CreateQuestionInput(),
                    new ToolPermissionContext("call-sequential"),
                    cancellationToken
                )
                .AsTask()
        );

        // Act
        using (accessor.Push(firstChannel))
        {
            await bridge.BindRunAsync(
                [],
                session: null,
                options: null,
                innerAgent,
                TestContext.Current.CancellationToken
            );
        }
        using (accessor.Push(secondChannel))
        {
            await bridge.BindRunAsync(
                [],
                session: null,
                options: null,
                innerAgent,
                TestContext.Current.CancellationToken
            );
        }

        // Assert
        Assert.Single(firstChannel.Requests);
        Assert.Single(secondChannel.Requests);
    }

    [Fact]
    public async Task HandleAsync_WhenUserCancels_ReturnsNonInterruptingDeny()
    {
        // Arrange
        var accessor = new HumanInteractionContextAccessor();
        var channel = new TestHumanInteractionChannel(request => new UserInputResponse
        {
            InteractionId = "test-interaction",
            Cancelled = true,
            ResponseData = null,
        });
        var bridge = new ClaudeCodeAskUserQuestionBridge(accessor, allowInteraction: true);
        PermissionResult? permissionResult = null;
        var innerAgent = new CallbackAgent(async cancellationToken =>
        {
            permissionResult = await bridge.HandleAsync(
                "AskUserQuestion",
                CreateQuestionInput(),
                new ToolPermissionContext("call-cancel"),
                cancellationToken
            );
        });

        // Act
        using (accessor.Push(channel))
        {
            await bridge.BindRunAsync(
                [],
                session: null,
                options: null,
                innerAgent,
                TestContext.Current.CancellationToken
            );
        }

        // Assert
        var deny = Assert.IsType<PermissionResultDeny>(permissionResult);
        Assert.Contains("cancelled", deny.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(deny.Interrupt);
    }

    [Fact]
    public async Task HandleAsync_ForNonQuestionPermission_ReturnsDeny()
    {
        // Arrange
        var bridge = new ClaudeCodeAskUserQuestionBridge(new HumanInteractionContextAccessor(), allowInteraction: true);

        // Act
        var result = await bridge.HandleAsync(
            "Bash",
            CreateQuestionInput(),
            new ToolPermissionContext("call-denied"),
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.IsType<PermissionResultDeny>(result);
    }

    [Fact]
    public async Task BindRunAsync_ForBackgroundAgent_DoesNotExposeInteractiveChannel()
    {
        // Arrange
        var accessor = new HumanInteractionContextAccessor();
        var channel = CreateAnsweringChannel("Yes");
        var bridge = new ClaudeCodeAskUserQuestionBridge(accessor, allowInteraction: false);
        PermissionResult? permissionResult = null;
        var innerAgent = new CallbackAgent(async cancellationToken =>
        {
            permissionResult = await bridge.HandleAsync(
                "AskUserQuestion",
                CreateQuestionInput(),
                new ToolPermissionContext("call-background"),
                cancellationToken
            );
        });

        // Act
        using (accessor.Push(channel))
        {
            await bridge.BindRunAsync(
                [],
                session: null,
                options: null,
                innerAgent,
                TestContext.Current.CancellationToken
            );
        }

        // Assert
        Assert.IsType<PermissionResultDeny>(permissionResult);
        Assert.Empty(channel.Requests);
    }

    private static TestHumanInteractionChannel CreateAnsweringChannel(string answer) =>
        new(request => new UserInputResponse
        {
            InteractionId = "test-interaction",
            Cancelled = false,
            ResponseData = JsonSerializer.SerializeToElement(
                new { answers = new Dictionary<string, string> { ["Continue?"] = answer } }
            ),
        });

    private static JsonElement CreateQuestionInput() =>
        JsonSerializer.SerializeToElement(
            new
            {
                questions = new[]
                {
                    new
                    {
                        question = "Continue?",
                        header = "Next step",
                        multiSelect = false,
                        options = new[]
                        {
                            new { label = "Yes", description = "Continue the task." },
                            new { label = "No", description = "Stop here." },
                        },
                    },
                },
            }
        );

    [Theory]
    [InlineData(AgwPermissionMode.AlwaysAsk, true, 2)]
    [InlineData(AgwPermissionMode.AllowSameArguments, true, 1)]
    [InlineData(AgwPermissionMode.AllowSameArguments, false, 2)]
    [InlineData(AgwPermissionMode.FullAccess, true, 0)]
    public async Task ToolApproval_ModeAndDecision_ControlReuse(
        AgwPermissionMode mode,
        bool approve,
        int expectedRequests
    )
    {
        var accessor = new HumanInteractionContextAccessor();
        var turnAccessor = new RuntimeTurnContextAccessor();
        var channel = new ToolChannel(approve);
        var settings = ExecutionSettings.CreateDefault().WithPermissionMode(mode);
        var task = new AgentExecutionTask
        {
            ProjectId = Guid.NewGuid(),
            ProjectConversationId = Guid.NewGuid(),
            ContextId = "scope",
        };
        var turn = new RuntimeTurnContext(
            settings,
            task,
            new ExecutionTarget(Guid.NewGuid(), AgentRuntimeType.Agent),
            "/workspace",
            new NullSink()
        )
        {
            UserId = "owner",
        };
        var cache = new Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode.ClaudeToolApprovalCache();
        var bridge = new ClaudeCodeAskUserQuestionBridge(
            accessor,
            true,
            mode,
            "/workspace",
            turn.AgentId,
            turnAccessor,
            cache
        );
        var results = new List<PermissionResult>();
        var agent = new CallbackAgent(async token =>
            results.Add(
                await bridge.HandleAsync(
                    "Bash",
                    JsonSerializer.SerializeToElement(new { command = "test" }),
                    new("call"),
                    token
                )
            )
        );
        using var turnScope = turnAccessor.Push(turn);
        using var interactionScope = accessor.Push(channel);
        await bridge.BindRunAsync([], null, null, agent, TestContext.Current.CancellationToken);
        await bridge.BindRunAsync([], null, null, agent, TestContext.Current.CancellationToken);
        Assert.Equal(expectedRequests, channel.Count);
        Assert.All(
            results,
            result => Assert.Equal(approve || mode == AgwPermissionMode.FullAccess, result is PermissionResultAllow)
        );
    }

    [Fact]
    public void ToolApprovalCache_ChangedArgumentsScopeOrVersion_RequiresApproval()
    {
        var cache = new Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode.ClaudeToolApprovalCache();
        var args = System.Text.Json.Nodes.JsonNode.Parse("{\"a\":1,\"b\":2}");
        cache.Add("user/project/conversation/agent/node/workdir", 0, "Bash", args);
        Assert.True(
            cache.Contains(
                "user/project/conversation/agent/node/workdir",
                0,
                "Bash",
                System.Text.Json.Nodes.JsonNode.Parse("{\"b\":2,\"a\":1}")
            )
        );
        Assert.False(cache.Contains("other-scope", 0, "Bash", args));
        Assert.False(cache.Contains("user/project/conversation/agent/node/workdir", 0, "Write", args));
        Assert.False(
            cache.Contains(
                "user/project/conversation/agent/node/workdir",
                0,
                "Bash",
                System.Text.Json.Nodes.JsonNode.Parse("{\"a\":2,\"b\":2}")
            )
        );
        Assert.False(cache.Contains("user/project/conversation/agent/node/workdir", 2, "Bash", args));
        Assert.False(cache.Contains("user/project/conversation/agent/node/workdir", 0, "Bash", args));
    }

    private sealed class NullSink : IExecutionMessageSink
    {
        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class ToolChannel : IHumanInteractionChannel, IInteractionHandler
    {
        private readonly bool _approve;

        public ToolChannel(bool approve)
        {
            _approve = approve;
        }

        public int Count { get; private set; }

        public ValueTask<UserInputResponse> RequestAsync(
            UserInputRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public ValueTask<InteractionResolution> ResolveAsync(
            InteractionRequest request,
            CancellationToken cancellationToken
        )
        {
            Count++;
            return ValueTask.FromResult<InteractionResolution>(
                new InteractionResolution.Resolved(
                    new ToolApprovalDecision { InteractionId = request.InteractionId, Approved = _approve }
                )
            );
        }
    }

    private sealed class TestHumanInteractionChannel : IHumanInteractionChannel
    {
        private readonly Func<UserInputRequest, UserInputResponse> _respond;

        public TestHumanInteractionChannel(Func<UserInputRequest, UserInputResponse> respond)
        {
            _respond = respond;
        }

        public List<UserInputRequest> Requests { get; } = [];

        public ValueTask<UserInputResponse> RequestAsync(UserInputRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(_respond(request));
        }
    }

    private sealed class CallbackAgent : AIAgent
    {
        private readonly Func<CancellationToken, Task> _callback;

        public CallbackAgent(Func<CancellationToken, Task> callback)
        {
            _callback = callback;
        }

        public override string? Name => "Callback";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new CallbackSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new CallbackSession());

        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        )
        {
            await _callback(cancellationToken);
            return new AgentResponse([]);
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await _callback(cancellationToken);
            yield break;
        }

        private sealed class CallbackSession : AgentSession;
    }
}
