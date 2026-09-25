using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Agw.Tools.HumanInteraction;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed class UnattendedAgentExecutionTests : IDisposable
{
    private readonly IDisposable _executionScope = ExecutionTestScopes.Scope().Push();

    public void Dispose() => _executionScope.Dispose();

    [Fact]
    public async Task RunAsync_UnattendedApprovalRequest_FailsExplicitly()
    {
        var agent = new UnattendedTestAgent(includeApproval: true);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            RunUnattendedAsync(agent, permissionMode: null, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
        Assert.Contains("unattended", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_UnattendedToolMessage_PersistsBeforeReturning()
    {
        var agent = new UnattendedTestAgent(includeApproval: false);
        var history = new RecordingConversationHistoryWriter();

        var messages = await RunUnattendedAsync(
            agent,
            permissionMode: null,
            TestContext.Current.CancellationToken,
            history
        );

        Assert.NotEmpty(messages);
        var warning = Assert.Single(history.Messages);
        Assert.Equal(AgwMessageTypes.ToolWarning, warning.AdditionalProperties!["type"]?.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task RunAsync_UnattendedFullAccess_ExecutesAfterApprovalAndPersists(int approvalRounds)
    {
        var agent = new UnattendedTestAgent(includeApproval: true) { ApprovalRounds = approvalRounds };
        var history = new RecordingConversationHistoryWriter();

        var messages = await RunUnattendedAsync(
            agent,
            AgwPermissionMode.FullAccess,
            TestContext.Current.CancellationToken,
            history
        );

        Assert.Equal(approvalRounds, agent.ExecutedTools);
        Assert.Contains(
            messages,
            message => message.Contents.OfType<AgwTextContent>().Any(text => text.Content == "done")
        );
        Assert.NotEmpty(history.Messages);
    }

    [Theory]
    [InlineData("question")]
    [InlineData("failure")]
    [InlineData("limit")]
    [InlineData("cancel")]
    public async Task RunAsync_UnattendedFullAccess_DoesNotHideFailures(string scenario)
    {
        var agent = new UnattendedTestAgent(true)
        {
            ToolName = scenario == "question" ? "ask_user_question" : "run_shell",
            FailAfterApproval = scenario == "failure",
            RequiresInput = scenario == "question",
            ApprovalRounds = scenario == "limit" ? 100 : 1,
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (scenario == "cancel")
            cancellation.Cancel();

        var error = await Record.ExceptionAsync(() =>
            RunUnattendedAsync(agent, AgwPermissionMode.FullAccess, cancellation.Token)
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

    /// <summary>
    /// 按无人值守协调方式执行一个 Turn：Agent 管线的批次层按权限自动批准工具审批，其余人工请求使执行失败。
    /// Runs one turn the way unattended coordination does: the Agent pipeline's batch layer approves tools by permission and other human requests fail the execution.
    /// </summary>
    private static async Task<List<AgwMessage>> RunUnattendedAsync(
        UnattendedTestAgent agent,
        AgwPermissionMode? permissionMode,
        CancellationToken cancellationToken,
        IConversationHistoryWriter? history = null
    )
    {
        var pipeline = new MafApprovalBatchAgent(
            agent,
            new HumanInteractionContextAccessor(new AgentExecutionContextAccessor()),
            []
        );
        var session = await pipeline.CreateSessionAsync(cancellationToken);
        await using var runtime = new AgentRuntime(
            NullLogger.Instance,
            pipeline,
            session,
            Guid.CreateVersion7(),
            "unattended-context",
            sessionStateScope: null,
            conversationHistoryWriter: history
        );
        var scope = ExecutionTestScopes.Scope(ExecutionTestScopes.Context(permissionMode: permissionMode));
        scope.BindInteractions(new UnattendedInteractionHandler(), null, null);
        var executor = new AgentTurnExecutor(null, null, null, NullLogger<AgentTurnExecutor>.Instance);
        var messages = new List<AgwMessage>();
        await foreach (
            var message in scope.RunStreaming(
                executor.RunAsync(
                    scope,
                    runtime,
                    new TurnInput(new AgwUserInput { Contents = [new AgwTextContent { Content = "run" }] }),
                    cancellationToken
                )
            )
        )
        {
            messages.Add(message);
        }
        return messages;
    }

    private sealed class RecordingConversationHistoryWriter : IConversationHistoryWriter
    {
        public List<ChatMessage> Messages { get; } = [];

        public Task AppendAsync(
            Guid projectId,
            string contextId,
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken = default
        )
        {
            Messages.AddRange(messages);
            return Task.CompletedTask;
        }
    }

    private sealed class UnattendedTestAgent : AIAgent
    {
        private readonly bool _includeApproval;
        public int ExecutedTools { get; private set; }
        public int ApprovalRounds { get; set; } = 1;
        public string ToolName { get; set; } = "run_shell";
        public bool FailAfterApproval { get; set; }
        public bool RequiresInput { get; set; }

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
                    ["type"] = AgwMessageTypes.ToolWarning,
                    ["persistSeparately"] = true,
                },
            };

            var approved = messages
                .SelectMany(message => message.Contents)
                .OfType<ToolApprovalResponseContent>()
                .Any(response => response.Approved);
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
                var request = new ToolApprovalRequestContent(
                    "approval-1",
                    new FunctionCallContent("call-1", ToolName, new Dictionary<string, object?>())
                );
                if (RequiresInput)
                    HumanInteractionToolMetadata.Write(
                        request,
                        new UserInputRequest("questions", "Input required", JsonSerializer.SerializeToElement(new { }))
                    );
                yield return new AgentResponseUpdate(ChatRole.Assistant, [request]);
                yield break;
            }

            yield return new AgentResponseUpdate(ChatRole.Assistant, "done");
        }

        private sealed class UnattendedTestSession : AgentSession;
    }
}
