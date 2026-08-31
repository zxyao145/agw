using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using PiAgentSdk.Internal;
using Xunit;

namespace PiAgentSdk.Tests;

public sealed class PiSessionTests
{
    [Fact]
    public void StartSession_InvalidExecutable_DoesNotProbeCli()
    {
        // Arrange
        var agent = new PiAgent(new PiAgentOptions { PiPathOverride = "/missing/pi" });

        // Act
        var session = agent.StartSession();

        // Assert
        Assert.Null(session.Id);
    }

    [Fact]
    public async Task RunAsync_WaitsForAgentSettledAndAggregatesUsage()
    {
        // Arrange
        var transport = new FakePiTransport();
        transport.OnWrite = line => HandleStandardFlow(transport, line);
        var agent = CreateAgent(transport);
        await using var session = agent.StartSession();

        // Act
        var turn = await session.RunAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("done", turn.FinalResponse);
        Assert.Equal(10, turn.Usage!.Input);
        Assert.Equal(3, turn.Usage.Output);
        Assert.Contains(transport.WrittenLines, line => line.Contains("\"type\":\"get_state\""));
        Assert.Contains(transport.WrittenLines, line => line.Contains("\"type\":\"prompt\""));
    }

    [Fact]
    public async Task EnsureStartedAsync_ResumeIdMismatch_ThrowsProtocolException()
    {
        // Arrange
        var transport = new FakePiTransport();
        transport.OnWrite = line =>
        {
            var command = JsonDocument.Parse(line).RootElement;
            if (command.GetProperty("type").GetString() == "get_state")
            {
                transport.Emit(Response(command, """{"sessionId":"different"}"""));
            }
        };
        var agent = CreateAgent(transport);
        await using var session = agent.ResumeSession("expected");

        // Act & Assert
        await Assert.ThrowsAsync<PiProtocolException>(() =>
            session.EnsureStartedAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task EnsureStartedAsync_NoResponse_ThrowsConfiguredTimeout()
    {
        // Arrange
        var transport = new FakePiTransport();
        var agent = CreateAgent(transport, new PiAgentOptions { CommandTimeout = TimeSpan.FromMilliseconds(30) });
        await using var session = agent.StartSession();

        // Act & Assert
        await Assert.ThrowsAsync<PiCommandTimeoutException>(() =>
            session.EnsureStartedAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task EnsureStartedAsync_ProcessExitsBeforeResponse_ThrowsProcessExit()
    {
        // Arrange
        var transport = new FakePiTransport();
        transport.OnWrite = _ => transport.Exit(7);
        var agent = CreateAgent(transport);
        await using var session = agent.StartSession();

        // Act
        var exception = await Assert.ThrowsAsync<PiProcessExitException>(() =>
            session.EnsureStartedAsync(TestContext.Current.CancellationToken)
        );

        // Assert
        Assert.Equal(7, exception.ExitCode);
    }

    [Fact]
    public async Task RunStreamingAsync_ConcurrentRun_ThrowsBusy()
    {
        // Arrange
        var transport = new FakePiTransport();
        transport.OnWrite = line =>
        {
            var command = JsonDocument.Parse(line).RootElement;
            var type = command.GetProperty("type").GetString();
            if (type == "get_state")
            {
                transport.Emit(Response(command, """{"sessionId":"session-1"}"""));
            }
            else if (type == "prompt")
            {
                transport.Emit(Response(command, data: null));
            }
        };
        var agent = CreateAgent(transport);
        await using var session = agent.StartSession();
        await using var first = session
            .RunStreamingAsync("first", cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var firstMove = first.MoveNextAsync().AsTask();
        await WaitForAsync(
            () => transport.WrittenLines.Any(line => line.Contains("\"type\":\"prompt\"")),
            TestContext.Current.CancellationToken
        );
        await using var second = session
            .RunStreamingAsync("second", cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        // Act & Assert
        await Assert.ThrowsAsync<PiSessionBusyException>(() => second.MoveNextAsync().AsTask());

        transport.Emit("""{"type":"agent_settled"}""");
        Assert.True(await firstMove);
    }

    [Fact]
    public async Task RunStreamingAsync_CancelledRun_DrainsAndAllowsNextRun()
    {
        // Arrange
        var transport = new FakePiTransport();
        var promptCount = 0;
        transport.OnWrite = line =>
        {
            var command = JsonDocument.Parse(line).RootElement;
            var type = command.GetProperty("type").GetString();
            if (type == "get_state")
            {
                transport.Emit(Response(command, """{"sessionId":"session-1"}"""));
            }
            else if (type == "prompt")
            {
                promptCount++;
                transport.Emit(Response(command, data: null));
                if (promptCount == 2)
                {
                    transport.Emit(
                        """{"type":"turn_end","message":{"role":"assistant","content":[{"type":"text","text":"recovered"}],"usage":{"input":1,"output":1,"cacheRead":0,"cacheWrite":0,"totalTokens":2},"stopReason":"stop","timestamp":1},"toolResults":[]}"""
                    );
                    transport.Emit("""{"type":"agent_settled"}""");
                }
            }
            else if (type == "abort")
            {
                transport.Emit(Response(command, data: null));
                transport.Emit("""{"type":"agent_settled"}""");
            }
        };
        var agent = CreateAgent(transport);
        await using var session = agent.StartSession();
        using var cancellation = new CancellationTokenSource();
        await using var first = session
            .RunStreamingAsync("first", cancellationToken: cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        var firstMove = first.MoveNextAsync().AsTask();
        await WaitForAsync(
            () => transport.WrittenLines.Any(line => line.Contains("\"type\":\"prompt\"")),
            TestContext.Current.CancellationToken
        );

        // Act
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstMove);
        var recovered = await session.RunAsync("second", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.False(session.IsFaulted);
        Assert.Equal("recovered", recovered.FinalResponse);
        Assert.Contains(transport.WrittenLines, line => line.Contains("\"type\":\"abort\""));
    }

    [Fact]
    public async Task RunStreamingAsync_EarlyDisposeWithFullEventBuffer_DrainsBeforeAbortResponse()
    {
        // Arrange
        var transport = new FakePiTransport();
        transport.OnWrite = line =>
        {
            var command = JsonDocument.Parse(line).RootElement;
            switch (command.GetProperty("type").GetString())
            {
                case "get_state":
                    transport.Emit(Response(command, """{"sessionId":"session-1"}"""));
                    break;
                case "prompt":
                    transport.Emit(Response(command, data: null));
                    for (var index = 0; index < 300; index++)
                    {
                        transport.Emit("""{"type":"agent_start"}""");
                    }

                    break;
                case "abort":
                    transport.Emit(Response(command, data: null));
                    transport.Emit("""{"type":"agent_settled"}""");
                    break;
            }
        };
        var agent = CreateAgent(
            transport,
            new PiAgentOptions
            {
                CommandTimeout = TimeSpan.FromSeconds(1),
                AbortGracePeriod = TimeSpan.FromMilliseconds(250),
            }
        );
        await using var session = agent.StartSession();
        await using var enumerator = session
            .RunStreamingAsync("fill", cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        await WaitForAsync(() => transport.LinesRead >= 258, TestContext.Current.CancellationToken);

        // Act
        await enumerator
            .DisposeAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(transport.WrittenLines, line => line.Contains("\"type\":\"abort\""));
        Assert.Equal(0, transport.KillCount);
        Assert.False(session.IsFaulted);
    }

    private static PiAgent CreateAgent(FakePiTransport transport, PiAgentOptions? options = null) =>
        new(
            options ?? new PiAgentOptions { CommandTimeout = TimeSpan.FromSeconds(2) },
            logger: null,
            (_, _) => transport
        );

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(5, cancellationToken);
        }
    }

    private static void HandleStandardFlow(FakePiTransport transport, string line)
    {
        var command = JsonDocument.Parse(line).RootElement;
        switch (command.GetProperty("type").GetString())
        {
            case "get_state":
                transport.Emit(Response(command, """{"sessionId":"session-1","sessionFile":"/tmp/session.jsonl"}"""));
                break;
            case "prompt":
                transport.Emit(Response(command, data: null));
                transport.Emit("""{"type":"agent_start"}""");
                transport.Emit(
                    """{"type":"turn_end","message":{"role":"assistant","content":[{"type":"text","text":"done"}],"usage":{"input":10,"output":3,"cacheRead":0,"cacheWrite":0,"totalTokens":13},"stopReason":"stop","timestamp":1},"toolResults":[]}"""
                );
                transport.Emit("""{"type":"agent_end","messages":[],"willRetry":false}""");
                transport.Emit("""{"type":"agent_settled"}""");
                break;
        }
    }

    private static string Response(JsonElement command, string? data)
    {
        var id = command.GetProperty("id").GetString();
        var suffix = data == null ? string.Empty : $",\"data\":{data}";
        return $"{{\"type\":\"response\",\"id\":\"{id}\",\"command\":\"{command.GetProperty("type").GetString()}\",\"success\":true{suffix}}}";
    }

    private sealed class FakePiTransport : IPiProcessTransport
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource<PiProcessExitInfo> _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Action<string>? OnWrite { get; set; }

        public List<string> WrittenLines { get; } = [];

        public int LinesRead => Volatile.Read(ref _linesRead);

        public int KillCount { get; private set; }

        public string StandardErrorTail => string.Empty;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            WrittenLines.Add(line);
            OnWrite?.Invoke(line);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<string> ReadLinesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await foreach (var line in _output.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Increment(ref _linesRead);
                yield return line;
            }
        }

        public Task<PiProcessExitInfo> WaitForExitAsync(CancellationToken cancellationToken) =>
            _exit.Task.WaitAsync(cancellationToken);

        public ValueTask KillAsync(CancellationToken cancellationToken)
        {
            KillCount++;
            _exit.TrySetResult(new PiProcessExitInfo { ExitCode = -1 });
            _output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _exit.TrySetResult(new PiProcessExitInfo { ExitCode = 0 });
            _output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void Emit(string line) => _output.Writer.TryWrite(line);

        public void Exit(int exitCode)
        {
            _exit.TrySetResult(new PiProcessExitInfo { ExitCode = exitCode });
            _output.Writer.TryComplete();
        }

        private int _linesRead;
    }
}
