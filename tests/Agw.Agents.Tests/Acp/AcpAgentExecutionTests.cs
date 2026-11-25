using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agw.Agents.Application.Acp;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Projects.Application.History;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using static Agw.Agents.Tests.Acp.AcpAgentHarness;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpAgentExecutionTests
{
    [Fact]
    public async Task RunTurn_CustomCommand_StreamsMessageAndResult()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(new { turns = new[] { Turn(Text("hello "), Text("world")) } });
        var turn = await agent.RunTurnAsync();
        await agent.AssertCompletedAsync(turn);
        var history = await agent.ReadHistoryAsync();
        var answer = Assert.Single(
            history,
            row => row.Purpose == ConversationMessagePurpose.Message && row.ToChatMessage()?.Text == "hello world"
        );
        var result = Assert.Single(history, row => row.Purpose == ConversationMessagePurpose.Result).ToChatMessage();
        Assert.Equal(answer.Id.ToString("D"), result!.AdditionalProperties!["resultSourceMessageId"]?.ToString());
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(agent.Runtime.WorkspacePath),
            agent.Events("start")[0].GetProperty("cwd").GetString()
        );
    }

    [Fact]
    public async Task RunTurn_InstalledRegistryAgent_Launches()
    {
        using var user = EnterUser();
        var fixture = await AcpTestAgent.EnsureInstalledAsync();
        var (scenario, log) = fixture.CreateScenario(new { turns = new[] { Turn(Text("installed")) } });
        await using var agent = await CreateAsync("""{"registry":{"agentId":"test-agent"}}""");
        var installer = agent.Runtime.Services.GetRequiredService<IAcpAgentInstaller>();
        var distribution = new AcpPackageDistribution(await fixture.PackAsync(), [scenario, log], null);
        await installer.InstallAsync(
            new("test-agent", "Test agent", "1.0.0", "", null, null, null, new(null, distribution, null)),
            Ct
        );
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        var argv = fixture.ReadLog(log)[0].GetProperty("argv");
        Assert.StartsWith("node", Path.GetFileName(argv[0].GetString()));
        Assert.EndsWith("acp-test-agent.mjs", argv[1].GetString());
    }

    [Fact]
    public async Task RunTurn_RegistryAgentNotInstalled_ReturnsNotInstalled()
    {
        using var user = EnterUser();
        await using var agent = await CreateAsync("""{"registry":{"agentId":"missing"}}""");
        await agent.AssertFailureAsync(await agent.RunTurnAsync(), ErrorCodes.AcpAgentNotInstalled.Code, "missing");
    }

    [Fact]
    public async Task RunTurn_ThoughtAndToolWithDiff_PersistsReasoningCallAndResult()
    {
        using var user = EnterUser();
        object[] steps =
        [
            new { update = Chunk("thinking about this", "agent_thought_chunk") },
            new
            {
                update = new
                {
                    sessionUpdate = "tool_call",
                    toolCallId = "edit",
                    title = "Edit a.txt",
                    kind = "edit",
                    status = "in_progress",
                },
            },
            new
            {
                update = new
                {
                    sessionUpdate = "tool_call_update",
                    toolCallId = "edit",
                    status = "completed",
                    content = new[]
                    {
                        new
                        {
                            type = "diff",
                            path = "a.txt",
                            oldText = "old",
                            newText = "new",
                        },
                    },
                },
            },
            Text("done"),
        ];
        await using var agent = await ScenarioAsync(new { turns = new[] { Turn(steps) } });
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        var history = (await agent.ReadHistoryAsync()).Select(r => r.ToChatMessage()).OfType<ChatMessage>().ToList();
        Assert.Contains(
            history.SelectMany(m => m.Contents),
            c => c is TextReasoningContent { Text: "thinking about this" }
        );
        Assert.Contains(
            history,
            message =>
                message
                    .Contents.OfType<FunctionCallContent>()
                    .Any(call =>
                        call.CallId == "edit" && call.AdditionalProperties?["toolTitle"]?.ToString() == "Edit a.txt"
                    )
        );
        Assert.Contains(
            history.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            result => result.CallId == "edit" && result.Result?.ToString()?.Contains("+++ b/a.txt") == true
        );
    }

    [Fact]
    public async Task RunTurn_Plan_EmitsTodoSnapshot()
    {
        using var user = EnterUser();
        object Plan(string content) =>
            new
            {
                update = new
                {
                    sessionUpdate = "plan",
                    entries = new[]
                    {
                        new
                        {
                            content,
                            priority = "medium",
                            status = "completed",
                        },
                    },
                },
            };
        await using var agent = await ScenarioAsync(
            new { turns = new[] { Turn(Plan("original"), Plan("revised"), Text("done")) } }
        );
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        var plan = Assert.Single(
            await agent.ReadHistoryAsync(),
            row =>
                row.ToChatMessage()?.AdditionalProperties?.TryGetValue("type", out var type) == true
                && type?.ToString() == "tool-todo-snapshot"
        );
        Assert.Contains("revised", plan.ConversationPayload);
        Assert.DoesNotContain("original", plan.ConversationPayload);
    }

    [Fact]
    public async Task RunTurn_ModeAndConfigOptions_AppliedAfterSession()
    {
        using var user = EnterUser();
        var scenario = new
        {
            modes = new
            {
                currentModeId = "ask",
                availableModes = new[] { new { id = "ask", name = "Ask" }, new { id = "code", name = "Code" } },
            },
            configOptions = new object[]
            {
                new
                {
                    id = "model",
                    type = "select",
                    name = "Model",
                    currentValue = "a",
                    options = new[] { new { value = "a", name = "A" }, new { value = "b", name = "B" } },
                },
                new
                {
                    id = "fast",
                    type = "boolean",
                    name = "Fast",
                    currentValue = false,
                },
            },
            turns = new[] { Turn(Text("done")) },
        };
        await using var agent = await ScenarioAsync(
            scenario,
            new { modeId = "code", configOptions = new { model = "b", fast = true } }
        );
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        Assert.Equal(
            [
                "initialize",
                "session/new",
                "session/set_mode",
                "session/set_config_option",
                "session/set_config_option",
                "session/prompt",
            ],
            agent.Log.Where(r => r.TryGetProperty("method", out _)).Select(r => r.GetProperty("method").GetString())
        );
        var boolean = agent
            .Requests("session/set_config_option")
            .Single(request => request.GetProperty("configId").GetString() == "fast");
        Assert.True(boolean.GetProperty("value").GetBoolean());
        Assert.Equal("boolean", boolean.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("config", "model")]
    [InlineData("mode", "This ACP agent does not report session modes.")]
    [InlineData("terminal", "api-key")]
    public async Task RunTurn_InvalidSessionSetting_ReportsInvalidParam(string setting, string expected)
    {
        using var user = EnterUser();
        object extra = setting switch
        {
            "config" => new { configOptions = new { model = "missing" } },
            "mode" => new { modeId = "code" },
            _ => new { authMethodId = "interactive" },
        };
        object[] authMethods =
        [
            new { id = "api-key", name = "API Key" },
            new
            {
                id = "interactive",
                name = "Interactive",
                type = "terminal",
                args = Array.Empty<string>(),
            },
        ];
        await using var agent = await ScenarioAsync(new { authMethods }, extra);
        await agent.AssertFailureAsync(await agent.RunTurnAsync(), ErrorCodes.InvalidParam.Code, expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunTurn_AuthenticationRequired_UsesConfiguredMethod(bool configured)
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(
            new { requireAuth = true, turns = new[] { Turn(Text("done")) } },
            configured ? new { authMethodId = "api-key" } : null
        );
        var turn = await agent.RunTurnAsync();
        if (!configured)
        {
            await agent.AssertFailureAsync(turn, ErrorCodes.AcpAgentAuthenticationRequired.Code, "api-key");
            return;
        }
        await agent.AssertCompletedAsync(turn);
        Assert.Equal(
            ["initialize", "authenticate", "session/new", "session/prompt"],
            agent.Log.Where(r => r.TryGetProperty("method", out _)).Select(r => r.GetProperty("method").GetString())
        );
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunTurn_AdditionalDirectories_SentWhenSupported(bool supported)
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(
            new { capabilities = new { additionalDirectories = supported }, turns = new[] { Turn(Text("done")) } }
        );
        var path = Directory.CreateDirectory(Path.Combine(agent.Runtime.WorkspacePath, "additional")).FullName;
        await agent.Runtime.QueryAsync(async db =>
        {
            var project = await db.Projects.SingleAsync(p => p.Id == agent.Runtime.ProjectId, Ct);
            project.AdditionalDirectories = [new ProjectDirectory { Id = Guid.NewGuid(), Path = path }];
            return await db.SaveChangesAsync(Ct);
        });
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        var request = Assert.Single(agent.Requests("session/new"));
        Assert.Equal(supported, request.TryGetProperty("additionalDirectories", out var dirs));
        if (supported)
            Assert.Equal(path, Assert.Single(dirs.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task RunTurn_Diagnostics_RecordTurnAndProcessStart()
    {
        using var user = EnterUser();
        var spans = new ConcurrentBag<string>();
        var counters = new ConcurrentBag<(string, string?, string?)>();
        using var activity = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Agw.Agents.AcpClient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => spans.Add(a.OperationName),
        };
        ActivitySource.AddActivityListener(activity);
        using var meter = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Agw.Agents.AcpClient")
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        meter.SetMeasurementEventCallback<long>(
            (instrument, _, tags, _) =>
            {
                var values = tags.ToArray().ToDictionary();
                counters.Add(
                    (
                        instrument.Name,
                        values.GetValueOrDefault("outcome")?.ToString(),
                        values.GetValueOrDefault("source")?.ToString()
                    )
                );
            }
        );
        meter.Start();
        await using var agent = await ScenarioAsync(new { turns = new[] { Turn(Text("done")) } });
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        Assert.Contains(counters, x => x.Item1 == "agw.acp.client.turns" && x.Item2 == "completed");
        Assert.Contains(counters, x => x.Item1 == "agw.acp.client.process_starts" && x.Item3 == "command");
        foreach (var name in new[] { "acp.client.start", "acp.client.session", "acp.client.turn" })
            Assert.Contains(name, spans);
    }

    [Fact]
    public async Task RunTurn_RefusalStopReason_ClosesUnfinishedToolBeforeReportingTurnStopped()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(
            new
            {
                turns = new[]
                {
                    new
                    {
                        steps = new object[]
                        {
                            Text("partial"),
                            new
                            {
                                update = new
                                {
                                    sessionUpdate = "tool_call",
                                    toolCallId = "unfinished",
                                    title = "Edit",
                                    kind = "edit",
                                    status = "in_progress",
                                },
                            },
                        },
                        stopReason = "refusal",
                    },
                },
            }
        );
        await agent.AssertFailureAsync(
            await agent.RunTurnAsync(),
            ErrorCodes.AcpAgentTurnStopped.Code,
            "The ACP agent stopped the turn: refusal."
        );
        var messages = (await agent.ReadHistoryAsync()).Select(row => row.ToChatMessage()).OfType<ChatMessage>();
        var toolMessage = Assert.Single(
            messages,
            message => message.Contents.OfType<FunctionResultContent>().Any(content => content.CallId == "unfinished")
        );
        const string expected = "The ACP agent ended the turn before this tool call finished.";
        Assert.Equal(expected, Assert.Single(toolMessage.Contents.OfType<FunctionResultContent>()).Result?.ToString());
        Assert.Equal(expected, Assert.Single(toolMessage.Contents.OfType<ErrorContent>()).Message);
    }

    [Fact]
    public async Task RunTurn_EnvironmentVariables_ReachProcess()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(
            new { turns = new[] { Turn(Text("done")) } },
            env: new Dictionary<string, string> { ["ACP_AGENT_KEY"] = "value" }
        );
        const string secret = "AGW_ACP_TEST_SECRET";
        var previous = Environment.GetEnvironmentVariable(secret);
        try
        {
            Environment.SetEnvironmentVariable(secret, "host-only");
            await agent.AssertCompletedAsync(await agent.RunTurnAsync());
            var names = agent
                .Events("start")[0]
                .GetProperty("envNames")
                .EnumerateArray()
                .Select(key => key.GetString())
                .ToArray();
            Assert.Contains("ACP_AGENT_KEY", names);
            Assert.DoesNotContain(secret, names);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret, previous);
        }
    }

    [Fact]
    public async Task RunTurn_OtherSessionAndLateUpdates_StayOutOfBothTurns()
    {
        using var user = EnterUser();
        var first = new
        {
            steps = new object[]
            {
                new { updateForSession = "other", update = Chunk("foreign-content") },
                Text("first"),
            },
            afterResponse = Chunk("late-content"),
        };
        await using var agent = await ScenarioAsync(new { turns = new object[] { first, Turn(Text("second")) } });
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        var history = await agent.ReadHistoryAsync();
        Assert.DoesNotContain(
            history,
            row =>
                row.ConversationPayload?.Contains("foreign-content") == true
                || row.ConversationPayload?.Contains("late-content") == true
        );
    }

    [Fact]
    public async Task RunTurn_AgentflowNode_CreatesSessionPerInvocation()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(
            new { turns = new[] { Turn(Text("from node")), Turn(Text("from node again")) } }
        );
        var flows = agent.Runtime.Services.GetRequiredService<AgentflowAppService>();
        var definition = new AgentflowDefinition(
            [
                new()
                {
                    NodeId = "input",
                    Name = "Input",
                    Kind = AgentflowNodeKind.Input,
                    RoutingStrategy = AgentflowRoutingStrategy.FanOut,
                    ConfigJson = AgentflowDefinitionJson.EmptyOptions,
                },
                new()
                {
                    NodeId = "node",
                    Name = "ACP node",
                    Kind = AgentflowNodeKind.Agent,
                    RelateId = agent.Runtime.AgentId,
                    RoutingStrategy = AgentflowRoutingStrategy.Direct,
                    ConfigJson = AgentflowDefinitionJson.EmptyOptions,
                },
                new()
                {
                    NodeId = "output",
                    Name = "Output",
                    Kind = AgentflowNodeKind.Output,
                    RoutingStrategy = AgentflowRoutingStrategy.Direct,
                    ConfigJson = AgentflowDefinitionJson.WriteOptions(new OutputOptions(false)),
                },
            ],
            [
                new()
                {
                    EdgeId = "input-node",
                    SourceNodeId = "input",
                    TargetNodeId = "node",
                    ConditionJson = AgentflowDefinitionJson.WriteConditions([]),
                },
                new()
                {
                    EdgeId = "node-output",
                    SourceNodeId = "node",
                    TargetNodeId = "output",
                    ConditionJson = AgentflowDefinitionJson.WriteConditions([]),
                },
            ],
            []
        );
        var flow = await flows.CreateAsync("ACP flow", null, definition, Ct);
        await flows.PublishAsync(flow.Id, 1, Ct);
        foreach (var text in new[] { "first node invocation", "second node invocation" })
        {
            var id = await agent.Runtime.StartTurnAsync(
                text,
                agentId: flow.Id,
                runtimeType: AgentRuntimeType.Agentflow
            );
            await agent.WaitIdleAsync();
            await agent.AssertCompletedAsync(id);
        }
        Assert.Equal(2, agent.Requests("session/new").Count);
        Assert.Equal(
            2,
            agent.Requests("session/prompt").Select(r => r.GetProperty("sessionId").GetString()).Distinct().Count()
        );
        Assert.Null(await agent.ReadProviderSessionIdAsync());
    }

    [Fact]
    public async Task RunTurn_NonStreamingNode_AggregatesResult()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(new { turns = new[] { Turn(Text("node "), Text("answer")) } });
        var factory = agent.Runtime.Services.GetRequiredService<AgentRuntimeFactory>();
        var node = await factory.CreateAgentflowNodeAgentAsync(
            agent.Runtime.AgentId,
            agent.Runtime.ProjectId,
            agent.Runtime.ConversationId,
            null,
            true,
            Ct
        );
        Assert.NotNull(node);
        await using var resource = Assert.IsAssignableFrom<IAsyncDisposable>(node.Agent);
        var session = await node.Agent.CreateSessionAsync(Ct);
        var result = await node.Agent.RunAsync("node input", session, cancellationToken: Ct);
        Assert.Contains(result.Messages, m => m.Text == "node answer");
    }
}
