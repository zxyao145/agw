using Agw.Agents.Application.Execution;

namespace Agw.Agents.Tests;

public class RuntimeExecSessionTests
{
    [Fact]
    public async Task TryStartTurn_WhenTurnCompletes_ReleasesActiveTurn()
    {
        await using var session = new TestRuntimeExecSession();
        using var cts = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var started = session.TryStartTurn(new ActiveTurn(completion.Task, cts));
        completion.SetResult();
        await session.WhenIdleAsync();

        Assert.True(started);
        Assert.Null(session.ActiveTurn);
    }

    [Fact]
    public async Task TryStartTurn_WhenAnotherTurnIsRunning_ReturnsFalse()
    {
        await using var session = new TestRuntimeExecSession();
        using var firstCts = new CancellationTokenSource();
        using var secondCts = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstStarted = session.TryStartTurn(new ActiveTurn(completion.Task, firstCts));
        var secondStarted = session.TryStartTurn(new ActiveTurn(Task.CompletedTask, secondCts));

        Assert.True(firstStarted);
        Assert.False(secondStarted);

        completion.SetResult();
        await session.WhenIdleAsync();
    }

    [Fact]
    public async Task RequestInterrupt_ForwardsToActiveTurn()
    {
        await using var session = new TestRuntimeExecSession();
        using var cts = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = new ActiveTurn(completion.Task, cts);
        session.TryStartTurn(turn);

        session.RequestInterrupt("stop");

        Assert.True(turn.InterruptRequested);
        Assert.True(cts.IsCancellationRequested);

        completion.SetResult();
        await session.WhenIdleAsync();
    }

    private sealed class TestRuntimeExecSession : RuntimeExecSessionBase;
}
