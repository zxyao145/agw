using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agw.Agents.ExternalAgents.Acp;
using static Agw.Agents.Tests.Acp.AcpAgentHarness;

namespace Agw.Agents.Tests.Acp;

[CollectionDefinition(DisableParallelization = true)]
public sealed class AcpDiagnosticsCollection;

[Collection(typeof(AcpDiagnosticsCollection))]
public sealed class AcpPermissionDiagnosticsTests
{
    [Fact]
    public async Task OnRequestPermissionAsync_EarlyResponses_CountsEachDecisionAndTurnRequest()
    {
        using var user = EnterUser();
        var decisions = new ConcurrentQueue<(string?, string?, long)>();
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument == AcpClientDiagnostics.PermissionRequests)
                listener.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<long>(
            (_, count, tags, _) =>
            {
                var tagValues = tags.ToArray().ToDictionary();
                decisions.Enqueue((tagValues["decision"]?.ToString(), tagValues["mode"]?.ToString(), count));
            }
        );
        var activities = new ConcurrentDictionary<string, Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source == AcpClientDiagnostics.ActivitySource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities[activity.OperationName] = activity,
        };
        ActivitySource.AddActivityListener(listener);
        meter.Start();
        await using var agent = await ScenarioAsync(PermissionScenario());
        var turn = await agent.StartTurnAsync(permission: AgwPermissionMode.FullAccess);
        await WaitUntilAsync(() =>
            agent.ReadMessages(turn).Any(m => m.Contents.OfType<AgwTextContent>().Any(c => c.Content == "ready"))
        );
        agent.Interrupt();
        await agent.WaitIdleAsync();
        Assert.Equal(4, agent.Events("permission").Count);
        Assert.Equal(
            [
                ("rejected", "FullAccess", 1L),
                ("rejected", "FullAccess", 1L),
                ("allowed", "FullAccess", 1L),
                ("cancelled", "FullAccess", 1L),
            ],
            decisions.ToArray()
        );
        Assert.Equal(4, activities["acp.client.turn"].GetTagItem("PermissionRequestCount"));
        var start = activities["acp.client.start"];
        Assert.Equal(OperatingSystem.IsWindows() ? "node.exe" : "node", start.GetTagItem("Command"));
        Assert.Equal("command", start.GetTagItem("Source"));
        Assert.Equal(agent.Events("start")[0].GetProperty("pid").GetInt32(), start.GetTagItem("ProcessId"));
    }

    private static object PermissionScenario()
    {
        var tool = new
        {
            toolCallId = "permission",
            title = "Read",
            kind = "read",
        };
        var options = new[]
        {
            new
            {
                optionId = "no",
                name = "Reject",
                kind = "reject_once",
            },
            new
            {
                optionId = "yes",
                name = "Allow",
                kind = "allow_once",
            },
        };
        var other = new
        {
            sessionId = "other",
            toolCall = tool,
            options,
        };
        var steps = new object[]
        {
            new { permission = other },
            new { permission = new { toolCall = tool, options } },
            Text("ready"),
            new { delayMs = 30000 },
            new { permission = new { toolCall = tool, options } },
        };
        return new { permissionBeforeSession = other, turns = new[] { Turn(steps) } };
    }
}
