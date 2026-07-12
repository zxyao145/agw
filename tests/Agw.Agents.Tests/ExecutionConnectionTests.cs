using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using Agw.Shared.AgwMsgVm;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class ExecutionConnectionTests
{
    [Fact]
    public async Task DetachAsync_IdleRuntime_DisposesAndRemovesImmediately()
    {
        var runtime = new TestRuntime();
        await using var connection = CreateConnection();
        connection.Runtime = runtime;
        var removed = false;

        await connection.DetachAsync(() => removed = true);

        Assert.True(runtime.Disposed);
        Assert.True(removed);
    }

    [Fact]
    public async Task DetachAsync_RunningTurn_RemovesAfterTurnCompletes()
    {
        var runtime = new TestRuntime();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        runtime.TryStartTurn(new ActiveTurn(completion.Task, cts));
        await using var connection = CreateConnection();
        connection.Runtime = runtime;

        await connection.DetachAsync(() => removed.TrySetResult());
        Assert.False(removed.Task.IsCompleted);

        completion.SetResult();
        await removed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(runtime.Disposed);
    }

    [Fact]
    public async Task DetachAsync_WaitingForHuman_InterruptsTurn()
    {
        var runtime = new TestRuntime();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        runtime.TryStartTurn(new ActiveTurn(completion.Task, cts));
        await using var connection = CreateConnection();
        connection.Runtime = runtime;
        connection.SetWaitingForHuman(true);

        await connection.DetachAsync(() => removed.TrySetResult());

        Assert.True(cts.IsCancellationRequested);
        completion.SetResult();
        await removed.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private static ExecutionConnection CreateConnection()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        return new ExecutionConnection(
            "connection",
            "user",
            provider.CreateAsyncScope(),
            new ExecutionCommandDispatcher([]),
            new NullSink(),
            CancellationToken.None,
            NullLogger.Instance);
    }

    private sealed class TestRuntime : RuntimeBase
    {
        public bool Disposed { get; private set; }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Disposed = true;
        }
    }

    private sealed class NullSink : IExecutionMessageSink
    {
        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
