using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Turns;
using Agw.Agents.ExternalAgents;
using Agw.Agents.ExternalAgents.ClaudeCode;
using Agw.Shared.Contracts.Agents;
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
        var channel = new TestHumanInteractionChannel(request => new HumanInteractionResponse(
            request.RequestId,
            Cancelled: false,
            JsonSerializer.SerializeToElement(
                new { answers = new Dictionary<string, string> { ["Continue?"] = "Yes" } }
            )
        ));
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
        Assert.Equal("questions", request.InteractionKind);
        Assert.Equal("AskUserQuestion", request.ToolName);
        Assert.Equal("call-1", request.CallId);
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
        var channel = new TestHumanInteractionChannel(request => new HumanInteractionResponse(
            request.RequestId,
            Cancelled: true,
            ResponseData: null
        ));
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
        new(request => new HumanInteractionResponse(
            request.RequestId,
            Cancelled: false,
            JsonSerializer.SerializeToElement(
                new { answers = new Dictionary<string, string> { ["Continue?"] = answer } }
            )
        ));

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

    private sealed class TestHumanInteractionChannel : IHumanInteractionChannel
    {
        private readonly Func<HumanInteractionRequest, HumanInteractionResponse> _respond;

        public TestHumanInteractionChannel(Func<HumanInteractionRequest, HumanInteractionResponse> respond)
        {
            _respond = respond;
        }

        public List<HumanInteractionRequest> Requests { get; } = [];

        public ValueTask<HumanInteractionResponse> RequestAsync(
            HumanInteractionRequest request,
            CancellationToken cancellationToken
        )
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
