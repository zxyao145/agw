using System.Diagnostics;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp.Protocol;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpConnectionTests
{
    private static readonly Dictionary<string, string> NoEnvironment = [];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StartAsync_TestAgent_InitializesWithProtocolVersion1()
    {
        var (connection, fixture, log) = await StartScenarioAsync(new { }, new Handler());
        await using var owned = connection;
        Assert.Equal(1, connection.Initialize.ProtocolVersion);
        var parameters = fixture.ReadLog(log).Single(x => x.TryGetProperty("method", out _)).GetProperty("params");
        Assert.Equal(1, parameters.GetProperty("protocolVersion").GetInt32());
        var capabilities = parameters.GetProperty("clientCapabilities");
        Assert.False(capabilities.GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        Assert.False(capabilities.GetProperty("terminal").GetBoolean());
        Assert.Equal(
            "{}",
            capabilities.GetProperty("session").GetProperty("configOptions").GetProperty("boolean").GetRawText()
        );
        Assert.Equal("agw", parameters.GetProperty("clientInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task StartAsync_InitializeRequiresAuthentication_ReportsCredentials()
    {
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            StartScenarioAsync(new { requireAuthAtInitialize = true }, new Handler())
        );
        Assert.Equal(ErrorCodes.AcpAgentAuthenticationRequired.Code, error.Code);
        Assert.Contains("credentials", error.Message);
    }

    [Fact]
    public async Task StartAsync_NeverResponds_TimesOutAndKills()
    {
        var path = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".pid");
        try
        {
            var ex = await Assert.ThrowsAsync<AgwException>(() =>
                StartAsync(
                    [
                        "-e",
                        "require('node:fs').writeFileSync(process.argv[1],String(process.pid));console.error('Waiting for login');setInterval(()=>{},1000)",
                        path,
                    ],
                    new Handler(),
                    new AcpAgentOptions { InitializeTimeout = TimeSpan.FromSeconds(2) }
                )
            );
            Assert.Equal(ErrorCodes.AcpAgentTimeout.Code, ex.Code);
            Assert.Contains("Waiting for login", ex.Message);
            var pid = int.Parse(File.ReadAllText(path));
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StartAsync_ProcessExitsEarly_ThrowsStartFailedWithStderr()
    {
        var ex = await Assert.ThrowsAsync<AgwException>(() =>
            StartAsync(["-e", "console.error('boom');process.exit(3)"], new Handler(), new())
        );
        Assert.Equal(ErrorCodes.AcpAgentStartFailed.Code, ex.Code);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task InvokeOrderedAsync_UpdatesBeforeResponse_AreDeliveredFirst()
    {
        var update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "hello" } };
        var scenario = new
        {
            turns = new[] { new { steps = Enumerable.Repeat(new { update }, 50), afterResponse = update } },
        };
        var handler = new Handler();
        var (connection, _, _) = await StartScenarioAsync(scenario, handler);
        await using var owned = connection;
        var captured = -1;
        await PromptAsync(connection, () => captured = handler.Updates.Count);
        Assert.Equal(50, captured);
    }

    [Fact]
    public async Task InvokeOrderedAsync_NonJsonStdoutLine_FaultsConnection()
    {
        var scenario = new
        {
            turns = new[] { new { steps = new object[] { new { stdout = "hello" }, new { delayMs = 30000 } } } },
        };
        var (connection, _, _) = await StartScenarioAsync(scenario, new Handler());
        await using var owned = connection;
        var ex = await Assert.ThrowsAsync<AgwException>(() => PromptAsync(connection));
        Assert.Equal(ErrorCodes.AcpAgentRequestFailed.Code, ex.Code);
        Assert.True(connection.IsFaulted);
    }

    [Fact]
    public async Task InvokeAsync_UnknownMethod_PreservesRemoteErrorCode()
    {
        var (connection, _, _) = await StartScenarioAsync(new { }, new Handler());
        await using var owned = connection;
        var failure = await Assert.ThrowsAsync<AgwException>(() =>
            connection.InvokeAsync<object>("unavailable/method", new { }, TimeSpan.FromSeconds(5), Token)
        );
        Assert.Equal(ErrorCodes.AcpAgentRequestFailed.Code, failure.Code);
        Assert.Equal(AcpRpcErrorCodes.MethodNotFound, Assert.IsType<AcpRpcException>(failure.InnerException).Code);
        Assert.Contains(AcpRpcErrorCodes.MethodNotFound.ToString(), failure.Message);
    }

    [Fact]
    public async Task InvokeOrderedAsync_UnknownMethodFromAgent_ReturnsMethodNotFound()
    {
        var scenario = new { turns = new[] { new { steps = new[] { new { readTextFile = "/file" } } } } };
        var (connection, fixture, log) = await StartScenarioAsync(scenario, new Handler());
        await using var owned = connection;
        await PromptAsync(connection);
        Assert.Contains(
            fixture.ReadLog(log),
            entry => entry.TryGetProperty("code", out var code) && code.GetInt32() == AcpRpcErrorCodes.MethodNotFound
        );
    }

    [Fact]
    public async Task InvokeAsync_RequestTimesOut_AgentReceivesAcpCancellation()
    {
        var (connection, fixture, log) = await StartScenarioAsync(new { delayNewMs = 30000 }, new Handler());
        await using var owned = connection;
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            connection.InvokeAsync<AcpNewSessionResponse>(
                "session/new",
                new AcpNewSessionRequest(AppContext.BaseDirectory, []),
                TimeSpan.FromMilliseconds(500),
                Token
            )
        );
        Assert.Equal(ErrorCodes.AcpAgentTimeout.Code, error.Code);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (
            !fixture
                .ReadLog(log)
                .Any(entry => entry.TryGetProperty("event", out var name) && name.GetString() == "requestCancelled")
        )
            await Task.Delay(20, timeout.Token);
    }

    [Fact]
    public async Task InvokeOrderedAsync_AgentCancelsPermissionRequest_CancelsInboundHandler()
    {
        var permission = new
        {
            toolCall = new { toolCallId = "call" },
            options = new[]
            {
                new
                {
                    optionId = "allow",
                    name = "Allow",
                    kind = "allow_once",
                },
            },
        };
        var scenario = new { turns = new[] { new { steps = new[] { new { permission, cancelPermissionMs = 50 } } } } };
        var (connection, fixture, log) = await StartScenarioAsync(scenario, new Handler { WaitForCancellation = true });
        await using var owned = connection;
        await PromptAsync(connection).WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Contains(
            fixture.ReadLog(log),
            entry =>
                entry.TryGetProperty("outcome", out var outcome)
                && outcome.GetProperty("outcome").GetString() == "cancelled"
        );
    }

    private static async Task PromptAsync(AcpConnection connection, Action? onResponse = null)
    {
        var session = await connection.InvokeAsync<AcpNewSessionResponse>(
            "session/new",
            new { cwd = AppContext.BaseDirectory, mcpServers = Array.Empty<object>() },
            TimeSpan.FromSeconds(10),
            Token
        );
        await connection.InvokeOrderedAsync<AcpPromptResponse>(
            "session/prompt",
            new { sessionId = session.SessionId, prompt = Array.Empty<object>() },
            Token,
            onResponse
        );
    }

    private static async Task<(AcpConnection, AcpTestAgent, string)> StartScenarioAsync(
        object scenario,
        Handler handler
    )
    {
        var fixture = await AcpTestAgent.EnsureInstalledAsync();
        var (path, log) = fixture.CreateScenario(scenario);
        var connection = await StartAsync([fixture.ScriptPath, path, log], handler, new());
        return (connection, fixture, log);
    }

    private static Task<AcpConnection> StartAsync(string[] args, Handler handler, AcpAgentOptions options) =>
        AcpConnection.StartAsync(
            new AcpLaunchSpec("node", args, NoEnvironment, "command", null),
            AppContext.BaseDirectory,
            NoEnvironment,
            options,
            handler,
            NullLogger.Instance,
            TimeProvider.System,
            Token
        );

    private sealed class Handler : IAcpClientHandler
    {
        public List<AcpSessionNotification> Updates { get; } = [];
        public bool WaitForCancellation { get; init; }

        public void OnSessionUpdate(AcpSessionNotification notification) => Updates.Add(notification);

        public async Task<AcpRequestPermissionResponse> OnRequestPermissionAsync(
            AcpRequestPermissionRequest request,
            CancellationToken ct
        )
        {
            if (WaitForCancellation)
            {
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var subscription = ct.Register(() => cancelled.TrySetResult());
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            }
            return new(new("cancelled"));
        }
    }
}
