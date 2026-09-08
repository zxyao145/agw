using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class UnattendedAgentExecutionTests
{
    [Fact]
    public async Task CollectStreamingMessagesAsync_ApprovalRequest_FailsExplicitly()
    {
        var agent = new UnattendedTestAgent(includeApproval: true);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var persistence = new ToolTurnPersistence(agent, session, (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await AgentRuntimeService.CollectStreamingMessagesAsync(
                agent,
                [new ChatMessage(ChatRole.User, "run a tool")],
                session,
                persistence,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
        Assert.Contains("unattended", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CollectStreamingMessagesAsync_ToolMessage_PersistsBeforeReturning()
    {
        var agent = new UnattendedTestAgent(includeApproval: false);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<ChatMessage>? persisted = null;
        var persistence = new ToolTurnPersistence(
            agent,
            session,
            (messages, _) =>
            {
                persisted = messages.ToList();
                return Task.CompletedTask;
            }
        );

        var messages = await AgentRuntimeService.CollectStreamingMessagesAsync(
            agent,
            [new ChatMessage(ChatRole.User, "run")],
            session,
            persistence,
            TestContext.Current.CancellationToken
        );

        Assert.NotEmpty(messages);
        var warning = Assert.Single(persisted!);
        Assert.Equal(ToolMessageTypes.Warning, warning.AdditionalProperties!["type"]?.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task CollectStreamingMessagesAsync_FullAccess_ExecutesAfterApprovalAndPersists(int approvalRounds)
    {
        var agent = new UnattendedTestAgent(includeApproval: true);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        agent.ApprovalRounds = approvalRounds;
        var persisted = new List<ChatMessage>();
        var persistence = new ToolTurnPersistence(
            agent,
            session,
            (messages, _) =>
            {
                persisted.AddRange(messages);
                return Task.CompletedTask;
            }
        );

        var messages = await AgentRuntimeService.CollectStreamingMessagesAsync(
            agent,
            [new ChatMessage(ChatRole.User, "run")],
            session,
            persistence,
            TestContext.Current.CancellationToken,
            UnattendedApprovalHandler.Create(PermissionMode.FullAccess)
        );

        Assert.Equal(approvalRounds, agent.ExecutedTools);
        Assert.Contains(
            messages,
            message => message.Contents.OfType<AgwTextContent>().Any(text => text.Content == "done")
        );
        Assert.NotEmpty(persisted);
    }

    [Theory]
    [InlineData("question")]
    [InlineData("failure")]
    [InlineData("limit")]
    [InlineData("cancel")]
    public async Task CollectStreamingMessagesAsync_FullAccess_DoesNotHideFailures(string scenario)
    {
        var agent = new UnattendedTestAgent(true)
        {
            ToolName = scenario == "question" ? "ask_user_question" : "run_shell",
            FailAfterApproval = scenario == "failure",
            ApprovalRounds = scenario == "limit" ? 100 : 1,
        };
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var persistence = new ToolTurnPersistence(agent, session, (_, _) => Task.CompletedTask);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (scenario == "cancel")
            cancellation.Cancel();

        var error = await Record.ExceptionAsync(() =>
            AgentRuntimeService.CollectStreamingMessagesAsync(
                agent,
                [new ChatMessage(ChatRole.User, "run")],
                session,
                persistence,
                cancellation.Token,
                UnattendedApprovalHandler.Create(PermissionMode.FullAccess)
            )
        );

        if (scenario == "cancel")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.Equal(0, agent.ExecutedTools);
        }
        else
        {
            var failure = Assert.IsType<AgwException>(error);
            Assert.Contains(
                scenario switch
                {
                    "question" => "ask_user_question",
                    "failure" => "tool failed",
                    _ => "limit",
                },
                failure.Message
            );
        }
    }

    private sealed class UnattendedTestAgent : AIAgent
    {
        private readonly bool _includeApproval;
        public int ExecutedTools { get; private set; }
        public int ApprovalRounds { get; set; } = 1;
        public string ToolName { get; set; } = "run_shell";
        public bool FailAfterApproval { get; set; }

        public UnattendedTestAgent(bool includeApproval)
        {
            _includeApproval = includeApproval;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new UnattendedTestSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new UnattendedTestSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.System, [new TextContent(string.Empty)])
            {
                AuthorName = "tools",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["type"] = ToolMessageTypes.Warning,
                    ["persistSeparately"] = true,
                },
            };

            var approved = messages
                .SelectMany(message => message.Contents)
                .OfType<AlwaysApproveToolApprovalResponseContent>()
                .Any();
            if (approved)
            {
                ExecutedTools++;
                if (FailAfterApproval)
                {
                    throw new AgwException(ErrorCodes.AgentExecutionFailed, "tool failed");
                }
            }
            if (_includeApproval && ExecutedTools < ApprovalRounds)
            {
                yield return new AgentResponseUpdate(
                    ChatRole.Assistant,
                    [
                        new ToolApprovalRequestContent(
                            "approval-1",
                            new FunctionCallContent("call-1", ToolName, new Dictionary<string, object?>())
                        ),
                    ]
                );
                yield break;
            }

            yield return new AgentResponseUpdate(ChatRole.Assistant, "done");
        }

        private sealed class UnattendedTestSession : AgentSession;
    }
}
