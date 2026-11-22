using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using global::A2A;
using Agw.Agents.Application.A2A;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Agents.ExternalAgents.A2A;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using static Agw.Agents.Tests.A2A.A2ARemoteAgentStreamingTests;

namespace Agw.Agents.Tests.A2A;

public sealed class A2AClientDiagnosticsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "completed", "completed")]
    [InlineData(true, "input_required", "input-required")]
    public async Task RunStreaming_TerminalStatus_RecordsTurnSpanAndCounter(
        bool requiresInput,
        string outcome,
        string taskState
    )
    {
        var handler = new ScriptedAgentHandler(
            async (context, queue, ct) =>
            {
                var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
                await updater.SubmitAsync(ct);
                if (requiresInput)
                    await updater.RequireInputAsync(Status(context, "your turn"), ct);
                else
                    await updater.CompleteAsync(Status(context, "done"), ct);
            }
        );
        await using var server = await StartAsync(handler);
        await using var client = new A2ARemoteAgentTestClient(server);
        using var captured = new Capture();

        await client.RunAsync("go", Ct);

        var span = Assert.Single(captured.Spans, span => span.OperationName == "a2a.client.turn");
        Assert.Equal(taskState, span.GetTagItem("a2a.task_state"));
        Assert.Equal(client.Session.TaskId, span.GetTagItem("a2a.task_id"));
        Assert.Equal(client.Session.ContextId, span.GetTagItem("a2a.context_id"));
        Assert.Equal(outcome, Assert.Single(captured.Counters, count => count.Name == "agw.a2a.client.turns").Outcome);
        Assert.Equal(1, Assert.Single(captured.Counters, count => count.Name == "agw.a2a.client.turns").Value);
        Assert.Equal(
            ProtocolBindingNames.HttpJson,
            Assert.Single(captured.Counters, count => count.Name == "agw.a2a.client.turns").Binding
        );
        Assert.True(Assert.Single(captured.Durations) > 0);
    }

    [Fact]
    public async Task RunStreaming_Unauthorized_RecordsUnauthorizedOutcome()
    {
        await using var server = await A2ATestAgentServer.StartAsync(
            new(ProtocolBindingNames.HttpJson, true, ScriptedAgentHandler.Echo())
            {
                RequiredInterfaceHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer interface" },
            },
            Ct
        );
        await using var client = new A2ARemoteAgentTestClient(server);
        using var captured = new Capture();

        Assert.Equal(
            ErrorCodes.A2ARemoteAgentUnauthorized.Code,
            (await Assert.ThrowsAsync<AgwException>(() => client.RunAsync("go", Ct))).Code
        );

        Assert.Equal("unauthorized", Assert.Single(captured.Counters).Outcome);
        Assert.Equal(
            ActivityStatusCode.Error,
            Assert.Single(captured.Spans, span => span.OperationName == "a2a.client.turn").Status
        );
    }

    [Fact]
    public async Task RunStreaming_Resubscribed_RecordsResubscribeCounter()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedAgentHandler(
            async (context, queue, ct) =>
            {
                var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
                await updater.SubmitAsync(ct);
                await updater.StartWorkAsync(cancellationToken: ct);
                await updater.AddArtifactAsync(
                    [Part.FromText("ready")],
                    "answer",
                    lastChunk: false,
                    cancellationToken: ct
                );
                await finish.Task.WaitAsync(ct);
                await updater.CompleteAsync(cancellationToken: ct);
            }
        );
        await using var server = await StartAsync(handler);
        await using var client = new A2ARemoteAgentTestClient(server);
        using var captured = new Capture();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        await foreach (var update in client.StreamAsync("go", timeout.Token))
        {
            if (update.Text == "ready" && !A2ARemoteAgentTestClient.IsResult(update))
            {
                if (update.MessageId!.EndsWith("#1", StringComparison.Ordinal))
                    finish.TrySetResult();
                else
                    await server.DropActiveStreamsAsync(timeout.Token);
            }
        }

        Assert.Equal(1, Assert.Single(captured.Counters, count => count.Name == "agw.a2a.client.resubscribes").Value);
        Assert.Equal(
            1,
            Assert
                .Single(captured.Spans, span => span.OperationName == "a2a.client.turn")
                .GetTagItem("a2a.resubscribe_count")
        );
    }

    [Fact]
    public async Task FetchAsync_InvalidCard_RecordsInvalidOutcome()
    {
        await using var server = await StartAsync(ScriptedAgentHandler.Echo());
        server.CardBodyOverride = "<html></html>";
        await using var services = A2AAgentCardClientTests.CreateServices();
        using var captured = new Capture();

        var client = services.GetRequiredService<IA2AAgentCardClient>();
        await Assert.ThrowsAsync<AgwException>(() =>
            client.FetchAsync(server.CardUrl, A2AEndpointCredentials.Empty, Ct)
        );

        Assert.Equal(
            "invalid",
            Assert.Single(captured.Counters, count => count.Name == "agw.a2a.client.card_fetches").Outcome
        );
        Assert.Equal(
            server.CardUrl.IdnHost,
            Assert.Single(captured.Spans, span => span.OperationName == "a2a.client.card").GetTagItem("server.address")
        );
    }

    [Fact]
    public async Task RunStreaming_WithSecretHeaderAndQuery_TagsContainNoSecret()
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer secret-1" };
        await using var server = await A2ATestAgentServer.StartAsync(
            new(ProtocolBindingNames.HttpJson, true, ScriptedAgentHandler.Echo())
            {
                RequiredInterfaceHeaders = headers,
            },
            Ct
        );
        var target = new Uri(server.InterfaceUrl.AbsoluteUri + "?key=secret-2");
        await using var client = new A2ARemoteAgentTestClient(
            server,
            target: target,
            credentials: A2AAgentCardClientTests.Credentials(target, "Authorization", headers["Authorization"])
        );
        using var captured = new Capture();

        await client.RunAsync("go", Ct);

        var span = Assert.Single(captured.Spans, span => span.OperationName == "a2a.client.turn");
        Assert.Equal(target.IdnHost, span.GetTagItem("server.address"));
        Assert.Equal("Bearer secret-1", Assert.Single(server.Requests).Headers["Authorization"]);
        Assert.EndsWith("?key=secret-2", Assert.Single(server.Requests).PathAndQuery);
        Assert.DoesNotContain(
            captured.Spans.SelectMany(activity => activity.TagObjects),
            tag => tag.Value?.ToString()?.Contains("secret-", StringComparison.Ordinal) == true
        );
    }

    private sealed class Capture : IDisposable
    {
        private readonly Activity _parent = new Activity("a2a-diagnostics-test").Start();
        private readonly ActivityListener _activities;
        private readonly MeterListener _meters = new();
        public ConcurrentQueue<Activity> Spans { get; } = new();
        public ConcurrentQueue<(string Name, long Value, string? Outcome, string? Binding)> Counters { get; } = new();
        public ConcurrentQueue<double> Durations { get; } = new();

        public Capture()
        {
            _activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name is A2AClientDiagnostics.SourceName or "A2A" or "System.Net.Http",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.TraceId == _parent.TraceId)
                        Spans.Enqueue(activity);
                },
            };
            ActivitySource.AddActivityListener(_activities);
            _meters.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == A2AClientDiagnostics.SourceName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _meters.SetMeasurementEventCallback<long>(
                (instrument, value, tags, _) =>
                {
                    if (Activity.Current?.TraceId == _parent.TraceId)
                    {
                        var labels = tags.ToArray().ToDictionary();
                        Counters.Enqueue(
                            (
                                instrument.Name,
                                value,
                                labels.GetValueOrDefault("outcome") as string,
                                labels.GetValueOrDefault("protocol_binding") as string
                            )
                        );
                    }
                }
            );
            _meters.SetMeasurementEventCallback<double>(
                (instrument, value, _, _) =>
                {
                    if (
                        Activity.Current?.TraceId == _parent.TraceId
                        && instrument.Name == "agw.a2a.client.turn.duration"
                    )
                        Durations.Enqueue(value);
                }
            );
            _meters.Start();
        }

        public void Dispose()
        {
            _meters.Dispose();
            _activities.Dispose();
            _parent.Dispose();
        }
    }
}
