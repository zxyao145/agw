using System.Text.Json;
using global::A2A;
using Agw.Shared.Data.Entities.Projects;
using static Agw.Agents.Tests.A2A.A2ARemoteAgentExecutionTests;
using static Agw.Agents.Tests.A2A.A2ARemoteAgentStreamingTests;

namespace Agw.Agents.Tests.A2A;

public sealed class A2ARemoteAgentTurnRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunTurn_Unauthorized_ReturnsUnauthorizedCode()
    {
        using var user = A2ARemoteAgentHarness.EnterUser();
        await using var server = await A2ATestAgentServer.StartAsync(
            new(ProtocolBindingNames.HttpJson, true, ScriptedAgentHandler.Echo())
            {
                RequiredInterfaceHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer interface" },
            },
            Ct
        );
        await using var harness = await A2ARemoteAgentHarness.CreateAsync(server);

        var turn = await harness.RunTurnAsync("go");

        var saved = await harness.ReadTurnAsync(turn);
        Assert.Equal(ProjectConversationTurnStatus.Failed, saved.Status);
        Assert.Equal("5020011", saved.ErrorCode);
        var messages = harness.ReadMessages(turn);
        Assert.Equal("5020011", messages[^1].AdditionalProperties?["errorCode"]);
        var error = Assert.Single(messages.SelectMany(message => message.Contents).OfType<AgwErrorContent>());
        Assert.Contains("401", error.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunTurn_InputRequiredThenReply_ContinuesSameTask(bool rebuildRuntime)
    {
        using var user = A2ARemoteAgentHarness.EnterUser();
        await using var server = await StartAsync(InputRequiredHandler());
        await using var harness = await A2ARemoteAgentHarness.CreateAsync(server);
        var first = await harness.RunTurnAsync("ask");
        var waiting = await harness.ReadSessionAsync();
        Assert.Equal(TaskState.InputRequired, waiting.TaskState);
        Assert.NotNull(waiting.ContextId);
        Assert.NotNull(waiting.TaskId);
        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(first)).Status);
        Assert.Contains(harness.ReadMessages(first), message => IsResult(message) && HasText(message, "Which city?"));
        if (rebuildRuntime)
        {
            await harness.RebuildCoordinatorAsync();
        }

        var second = await harness.RunTurnAsync("Paris");

        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(second)).Status);
        Assert.Equal(TaskState.Completed, (await harness.ReadSessionAsync()).TaskState);
        var request = A2ARemoteAgentTestClient.ReadInput(server.Requests[^1]);
        Assert.Equal(waiting.ContextId, request.ContextId);
        Assert.Equal(waiting.TaskId, request.TaskId);
    }

    [Fact]
    public async Task RunTurn_InterruptedInputRequiredReply_NextTurnStartsNewTask()
    {
        using var user = A2ARemoteAgentHarness.EnterUser();
        var continuationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartAsync(InputRequiredHandler(continuationStarted));
        await using var harness = await A2ARemoteAgentHarness.CreateAsync(server);
        await harness.RunTurnAsync("ask");
        var waiting = await harness.ReadSessionAsync();
        Assert.Equal(TaskState.InputRequired, waiting.TaskState);
        var reply = await harness.StartTurnAsync("Paris");
        await continuationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        harness.Interrupt();
        await harness.WaitIdleAsync();

        Assert.Equal(ProjectConversationTurnStatus.Interrupted, (await harness.ReadTurnAsync(reply)).Status);
        Assert.Equal(TaskState.Canceled, (await harness.ReadSessionAsync()).TaskState);
        Assert.Equal(waiting.TaskId, Assert.Single(server.CanceledTaskIds));
        var next = await harness.RunTurnAsync("new task");
        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(next)).Status);
        var request = A2ARemoteAgentTestClient.ReadInput(server.Requests[^1]);
        Assert.NotNull(waiting.ContextId);
        Assert.Equal(waiting.ContextId, request.ContextId);
        Assert.Null(request.TaskId);
    }

    [Fact]
    public async Task RunTurn_ContinuationRejectedWithTaskNotFound_ClearsRemoteState()
    {
        using var user = A2ARemoteAgentHarness.EnterUser();
        await using var server = await StartAsync(InputRequiredHandler(), ProtocolBindingNames.JsonRpc);
        await using var harness = await A2ARemoteAgentHarness.CreateAsync(server);
        await harness.RunTurnAsync("ask");
        var waiting = await harness.ReadSessionAsync();
        Assert.Equal(TaskState.InputRequired, waiting.TaskState);
        Assert.NotNull(waiting.TaskId);
        await server.TaskStore.DeleteTaskAsync(waiting.TaskId, Ct);

        var rejected = await harness.RunTurnAsync("Paris");

        var request = A2ARemoteAgentTestClient.ReadInput(server.Requests[^1]);
        Assert.Equal(waiting.TaskId, request.TaskId);
        var failed = await harness.ReadTurnAsync(rejected);
        Assert.Equal(ProjectConversationTurnStatus.Failed, failed.Status);
        Assert.Equal("5020010", failed.ErrorCode);
        var cleared = await harness.ReadSessionAsync();
        Assert.Null(cleared.ContextId);
        Assert.Null(cleared.TaskId);
        Assert.Null(cleared.TaskState);
        await harness.RunTurnAsync("new task");
        request = A2ARemoteAgentTestClient.ReadInput(server.Requests[^1]);
        Assert.Null(request.ContextId);
        Assert.Null(request.TaskId);
    }

    [Fact]
    public async Task RunTurn_StreamDropped_ResubscribesAndCompletes()
    {
        using var user = A2ARemoteAgentHarness.EnterUser();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartAsync(ScriptedAgentHandler.StreamArtifacts(finish.Task, partial: true));
        await using var harness = await A2ARemoteAgentHarness.CreateAsync(server);
        var turn = await harness.StartTurnAsync("go");
        await WaitForAsync(() => harness.ReadMessages(turn).Any(message => HasText(message, "partial")));

        await server.DropActiveStreamsAsync(Ct);
        await WaitForAsync(() => harness.ReadMessages(turn).Count(message => HasText(message, "partial")) == 2);
        finish.SetResult();
        await harness.WaitIdleAsync();

        var messages = harness.ReadMessages(turn);
        Assert.Single(messages, message => HasText(message, "one"));
        Assert.True(HasText(Assert.Single(messages, IsResult), "onepartialtwo"));
        Assert.Single(
            server.Requests,
            request => request.PathAndQuery.Contains("subscribe", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(turn)).Status);
        Assert.Equal(TaskState.Completed, (await harness.ReadSessionAsync()).TaskState);
    }

    [Fact]
    public async Task RunTurn_NonStreamingAgent_PollsUntilCompleted()
    {
        using var user = A2ARemoteAgentHarness.EnterUser();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartAsync(ScriptedAgentHandler.StreamArtifacts(finish.Task), streaming: false);
        await using var harness = await A2ARemoteAgentHarness.CreateAsync(server);

        var turn = await harness.StartTurnAsync("go");
        await WaitForAsync(() => server.Requests.Count(request => request.Method == "GET") >= 3);
        finish.SetResult();
        await harness.WaitIdleAsync();

        using var send = JsonDocument.Parse(Assert.Single(server.Requests, request => request.Method == "POST").Body);
        Assert.True(send.RootElement.GetProperty("configuration").GetProperty("returnImmediately").GetBoolean());
        var messages = harness.ReadMessages(turn);
        Assert.True(HasText(Assert.Single(messages, IsResult), "onetwo"));
        Assert.Single(messages, message => HasText(message, "two"));
        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(turn)).Status);
        Assert.Equal(TaskState.Completed, (await harness.ReadSessionAsync()).TaskState);
    }

    private static ScriptedAgentHandler InputRequiredHandler(TaskCompletionSource? continuationStarted = null) =>
        new(
            async (context, queue, ct) =>
            {
                var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
                if (context.IsContinuation)
                {
                    if (continuationStarted is not null)
                    {
                        continuationStarted.SetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    await updater.CompleteAsync(Status(context, "done"), ct);
                }
                else if (context.UserText == "ask")
                {
                    await updater.SubmitAsync(ct);
                    await updater.RequireInputAsync(Status(context, "Which city?"), ct);
                }
                else
                {
                    await new MessageResponder(queue, context.ContextId).ReplyAsync("new task", ct);
                }
            }
        );

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }
}
