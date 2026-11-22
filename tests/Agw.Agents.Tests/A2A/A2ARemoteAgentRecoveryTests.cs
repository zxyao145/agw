using System.Text.Json;
using global::A2A;
using Agw.Agents.Execution.Agents.ExternalAgents.A2A;
using Agw.Agents.ExternalAgents.A2A;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using static Agw.Agents.Tests.A2A.A2ARemoteAgentStreamingTests;

namespace Agw.Agents.Tests.A2A;

public sealed class A2ARemoteAgentRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_NonStreamingTask_PollsOnlyWhileActive(bool polls)
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedAgentHandler(
            async (context, queue, ct) =>
            {
                if (polls)
                {
                    var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
                    await updater.SubmitAsync(ct);
                    await updater.StartWorkAsync(Status(context, "working"), ct);
                    await finish.Task.WaitAsync(ct);
                    await updater.AddArtifactAsync([Part.FromText("done")], "answer", cancellationToken: ct);
                    await updater.CompleteAsync(cancellationToken: ct);
                }
                else
                {
                    await queue.EnqueueTaskAsync(
                        new AgentTask
                        {
                            Id = context.TaskId,
                            ContextId = context.ContextId,
                            Status = new global::A2A.TaskStatus { State = TaskState.Completed },
                            Artifacts = [new Artifact { ArtifactId = "answer", Parts = [Part.FromText("done")] }],
                        },
                        ct
                    );
                }
            }
        );
        await using var server = await StartAsync(handler, streaming: false);
        await using var client = new A2ARemoteAgentTestClient(server, Options());
        var running = client.RunAsync("go", Ct);
        if (polls)
        {
            await WaitForAsync(() => server.Requests.Count(request => request.Method == "GET") >= 2);
            finish.SetResult();
        }
        var updates = await running;

        using var sent = JsonDocument.Parse(server.Requests[0].Body);
        Assert.True(sent.RootElement.GetProperty("configuration").GetProperty("returnImmediately").GetBoolean());
        Assert.Equal(polls, server.Requests.Any(request => request.Method == "GET"));
        Assert.Equal("done", Assert.Single(updates, A2ARemoteAgentTestClient.IsResult).Text);
        Assert.Single(updates, update => update.Text == "done" && !A2ARemoteAgentTestClient.IsResult(update));
    }

    [Fact]
    public async Task Run_NonStreamingMessageReply_EmitsAssistantAndResult()
    {
        await using var server = await StartAsync(ScriptedAgentHandler.Echo(), streaming: false);
        await using var client = new A2ARemoteAgentTestClient(server, Options());

        var updates = await client.RunAsync("hello", Ct);

        Assert.Equal(2, updates.Count);
        Assert.Equal("hello", updates[0].Text);
        Assert.True(A2ARemoteAgentTestClient.IsResult(updates[1]));
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData(false, ProtocolBindingNames.HttpJson)]
    [InlineData(true, ProtocolBindingNames.JsonRpc)]
    [InlineData(true, ProtocolBindingNames.HttpJson)]
    public async Task RunStreaming_StreamDropped_CompletesWithoutReplayingArtifacts(
        bool finishesBeforeSubscribe,
        string binding
    )
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = ScriptedAgentHandler.StreamArtifacts(finish.Task, partial: !finishesBeforeSubscribe);
        await using var server = await StartAsync(handler, binding);
        await using var client = new A2ARemoteAgentTestClient(server, Options());
        var updates = new List<AgentResponseUpdate>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await foreach (var update in client.StreamAsync("go", timeout.Token))
        {
            updates.Add(update);
            var completeWhileDisconnected = finishesBeforeSubscribe && update.Text == "one";
            if (completeWhileDisconnected && update.RawRepresentation is TaskArtifactUpdateEvent firstArtifact)
            {
                await CompleteDisconnectedTaskAsync(server, firstArtifact.TaskId, finish, timeout.Token);
            }
            if (!finishesBeforeSubscribe && update.Text == "partial")
            {
                if (update.MessageId!.EndsWith("#1", StringComparison.Ordinal))
                    finish.TrySetResult();
                else
                    await server.DropActiveStreamsAsync(timeout.Token);
            }
        }

        Assert.Contains(
            server.Requests.Skip(1),
            request =>
                request.PathAndQuery.Contains("subscribe", StringComparison.OrdinalIgnoreCase)
                || request.Body.Contains("SubscribeToTask", StringComparison.Ordinal)
        );
        if (finishesBeforeSubscribe)
        {
            Assert.Contains(
                server.Requests.Skip(1),
                request => request.Method == "GET" || request.Body.Contains("GetTask", StringComparison.Ordinal)
            );
        }
        Assert.Equal(
            finishesBeforeSubscribe ? "onetwo" : "onepartialtwo",
            Assert.Single(updates, A2ARemoteAgentTestClient.IsResult).Text
        );
        Assert.Single(updates, update => update.Text == "one");
    }

    private static async Task CompleteDisconnectedTaskAsync(
        A2ATestAgentServer server,
        string taskId,
        TaskCompletionSource finish,
        CancellationToken cancellationToken
    )
    {
        await server.DropActiveStreamsAsync(cancellationToken);
        finish.SetResult();
        while ((await server.TaskStore.GetTaskAsync(taskId, cancellationToken))?.Status.State != TaskState.Completed)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    [Fact]
    public void Process_ResubscribedArtifactEvent_SkipsPreviouslyCompletedArtifact()
    {
        var turn = new A2ARemoteTurn(new A2ARemoteAgentSession());
        var update = new StreamResponse
        {
            ArtifactUpdate = new TaskArtifactUpdateEvent
            {
                TaskId = "task",
                ContextId = "context",
                LastChunk = true,
                Artifact = new Artifact { ArtifactId = "answer", Parts = [Part.FromText("one")] },
            },
        };
        Assert.Single(turn.Process(update));
        turn.ResubscribeCount++;

        Assert.Empty(turn.Process(update, recovered: true));
    }

    [Fact]
    public async Task RunStreaming_PeerDropsBeforeTaskId_ThrowsRequestFailed()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedAgentHandler(
            async (_, _, ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        );
        await using var server = await StartAsync(handler);
        await using var client = new A2ARemoteAgentTestClient(server, Options());
        var running = client.RunAsync("go", Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        await server.DropActiveStreamsAsync(Ct);
        var error = await Assert.ThrowsAsync<AgwException>(() => running);

        Assert.Equal(ErrorCodes.A2ARemoteAgentRequestFailed.Code, error.Code);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task RunStreaming_NoResponseHeadersBeforeDeadline_ThrowsTimeout()
    {
        var handler = new ScriptedAgentHandler((_, _, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        await using var server = await StartAsync(handler);
        var options = Options();
        options.RequestTimeout = TimeSpan.FromMilliseconds(250);
        await using var client = new A2ARemoteAgentTestClient(server, options);

        var error = await Assert.ThrowsAsync<AgwException>(() => client.RunAsync("go", Ct));

        Assert.Equal(ErrorCodes.A2ARemoteAgentTimeout.Code, error.Code);
    }

    [Fact]
    public async Task RunStreaming_IdleBeyondTimeoutAndPeerUnreachable_ThrowsTimeout()
    {
        await using var server = await StartAsync(WaitingHandler(false));
        var options = Options();
        options.StreamIdleTimeout = TimeSpan.FromMilliseconds(250);
        options.ResubscribeAttempts = 1;
        await using var client = new A2ARemoteAgentTestClient(server, options);
        var error = await Assert.ThrowsAsync<AgwException>(async () =>
        {
            await foreach (var update in client.StreamAsync("go", Ct))
            {
                if (IsWorking(update))
                {
                    _ = StopAfterIdleAsync(server);
                }
            }
        });

        Assert.Equal(ErrorCodes.A2ARemoteAgentTimeout.Code, error.Code);
        Assert.NotNull(client.Session.TaskId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunStreaming_Canceled_CancelsRemoteTaskAndStartsNewTaskNextTurn(bool continuation)
    {
        await using var server = await StartAsync(WaitingHandler(continuation));
        await using var client = new A2ARemoteAgentTestClient(server, Options());
        if (continuation)
        {
            await client.RunAsync("ask", Ct);
            Assert.Equal(TaskState.InputRequired, client.Session.TaskState);
        }
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in client.StreamAsync("go", canceled.Token))
                if (IsWorking(update))
                    canceled.Cancel();
        });

        Assert.Equal(TaskState.Canceled, client.Session.TaskState);
        Assert.Equal(client.Session.TaskId, Assert.Single(server.CanceledTaskIds));
        var context = client.Session.ContextId;
        await client.RunAsync("next", Ct);
        var request = A2ARemoteAgentTestClient.ReadInput(server.Requests[^1]);
        Assert.Equal(context, request.ContextId);
        Assert.Null(request.TaskId);
    }

    [Fact]
    public async Task RunStreaming_CancelRejectedByPeer_StillThrowsCanceled()
    {
        await using var server = await StartAsync(WaitingHandler(false));
        await using var client = new A2ARemoteAgentTestClient(server, Options());
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in client.StreamAsync("go", canceled.Token))
            {
                if (IsWorking(update))
                {
                    await server.DropActiveStreamsAsync(Ct);
                    await server.StopAsync(Ct);
                    canceled.Cancel();
                }
            }
        });
        Assert.Empty(server.CanceledTaskIds);
    }

    private static ScriptedAgentHandler WaitingHandler(bool awaitsInput) =>
        new(
            async (context, queue, ct) =>
            {
                var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
                if (!context.IsContinuation)
                    await updater.SubmitAsync(ct);
                if (awaitsInput && context.UserText == "ask")
                {
                    await updater.RequireInputAsync(Status(context, "reply"), ct);
                    return;
                }
                if (context.UserText == "next")
                {
                    await updater.CompleteAsync(Status(context, "done"), ct);
                    return;
                }
                await updater.StartWorkAsync(Status(context, "working"), ct);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        );

    private static bool IsWorking(AgentResponseUpdate update) =>
        update.Contents.Any(content => content is TextReasoningContent { Text: "working" });

    private static A2ARemoteAgentOptions Options() =>
        new()
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            StreamIdleTimeout = TimeSpan.FromSeconds(2),
            ResubscribeAttempts = 2,
        };

    private static async Task StopAfterIdleAsync(A2ATestAgentServer server)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        await server.DropActiveStreamsAsync(Ct);
        await server.StopAsync(Ct);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!condition())
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
    }
}
