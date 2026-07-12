using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Tests;

public class RuntimeBaseTests
{
    [Fact]
    public void ActiveTurn_InterruptApi_DoesNotExposeUnusedReasonOrState()
    {
        var requestInterrupt = typeof(ActiveTurn).GetMethod(nameof(ActiveTurn.RequestInterrupt));

        Assert.NotNull(requestInterrupt);
        Assert.Empty(requestInterrupt!.GetParameters());
        Assert.Null(typeof(ActiveTurn).GetProperty("InterruptRequested"));
    }

    [Fact]
    public async Task TryStartTurn_WhenTurnCompletes_ReleasesActiveTurn()
    {
        await using var runtime = new TestRuntime();
        using var cts = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var started = runtime.TryStartTurn(new ActiveTurn(completion.Task, cts));
        completion.SetResult();
        await runtime.WhenIdleAsync();

        Assert.True(started);
        Assert.Null(runtime.ActiveTurn);
    }

    [Fact]
    public async Task TryStartTurn_WhenAnotherTurnIsRunning_ReturnsFalse()
    {
        await using var runtime = new TestRuntime();
        using var firstCts = new CancellationTokenSource();
        using var secondCts = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstStarted = runtime.TryStartTurn(new ActiveTurn(completion.Task, firstCts));
        var secondStarted = runtime.TryStartTurn(new ActiveTurn(Task.CompletedTask, secondCts));

        Assert.True(firstStarted);
        Assert.False(secondStarted);

        completion.SetResult();
        await runtime.WhenIdleAsync();
    }

    [Fact]
    public async Task RequestInterrupt_ForwardsToActiveTurn()
    {
        await using var runtime = new TestRuntime();
        using var cts = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptActionCalled = false;
        var turn = new ActiveTurn(completion.Task, cts, () => interruptActionCalled = true);
        runtime.TryStartTurn(turn);

        runtime.RequestInterrupt();

        Assert.True(interruptActionCalled);
        Assert.True(cts.IsCancellationRequested);

        completion.SetResult();
        await runtime.WhenIdleAsync();
    }

    [Fact]
    public async Task StartTurn_ExecutionTask_UsesTurnContextScope()
    {
        await using var runtime = new TestRuntime();
        var accessor = new RuntimeTurnContextAccessor();
        var context = new RuntimeTurnContext(
            new SettingCommand(Guid.NewGuid()),
            "user",
            "/workspace",
            new NullSink());
        RuntimeTurnContext? captured = null;
        using var cts = new CancellationTokenSource();

        var turn = runtime.StartTurn(
            context,
            accessor,
            cts,
            interruptAction: () => { },
            executeAsync: _ =>
            {
                captured = accessor.Current;
                return Task.CompletedTask;
            });

        Assert.NotNull(turn);
        await runtime.WhenIdleAsync();
        Assert.Same(context, captured);
        Assert.Null(accessor.Current);
    }

    private sealed class TestRuntime : RuntimeBase;

    private sealed class NullSink : IExecutionMessageSink
    {
        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
