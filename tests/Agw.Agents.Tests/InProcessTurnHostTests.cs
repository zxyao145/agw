using Agw.Agents.Execution.Agentflows.Runtime;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.InProcess;

namespace Agw.Agents.Tests;

public class InProcessTurnHostTests
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
    public async Task StartTurn_WhenTurnCompletes_ReleasesActiveTurn()
    {
        await using var host = CreateHost();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var turn = host.StartTurn(Scope(), new CancellationTokenSource(), () => { }, _ => completion.Task);
        completion.SetResult();
        await host.WhenIdleAsync();

        Assert.NotNull(turn);
        Assert.Null(host.ActiveTurn);
    }

    [Fact]
    public async Task StartTurn_WhenAnotherTurnIsRunning_ReturnsNull()
    {
        await using var host = CreateHost();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondExecuted = false;

        var first = host.StartTurn(Scope(), new CancellationTokenSource(), () => { }, _ => completion.Task);
        var second = host.StartTurn(
            Scope(),
            new CancellationTokenSource(),
            () => { },
            _ =>
            {
                secondExecuted = true;
                return Task.CompletedTask;
            }
        );

        Assert.NotNull(first);
        Assert.Null(second);

        completion.SetResult();
        await host.WhenIdleAsync();
        Assert.False(secondExecuted);
    }

    [Fact]
    public async Task RequestInterrupt_ForwardsToActiveTurn()
    {
        await using var host = CreateHost();
        var cts = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptActionCalled = false;
        host.StartTurn(Scope(), cts, () => interruptActionCalled = true, _ => completion.Task);

        host.RequestInterrupt();

        Assert.True(interruptActionCalled);
        Assert.True(cts.IsCancellationRequested);

        completion.SetResult();
        await host.WhenIdleAsync();
    }

    [Fact]
    public async Task TryScheduleAfterTurn_SameKeyRunsOnlyLatestActionBeforeIdle()
    {
        await using var host = CreateHost();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<string>();
        host.StartTurn(Scope(), new CancellationTokenSource(), () => { }, _ => completion.Task);

        Assert.True(
            host.TryScheduleAfterTurn(
                "mode",
                _ =>
                {
                    applied.Add("plan");
                    return Task.CompletedTask;
                }
            )
        );
        Assert.True(
            host.TryScheduleAfterTurn(
                "mode",
                _ =>
                {
                    applied.Add("execute");
                    return Task.CompletedTask;
                }
            )
        );

        completion.SetResult();
        await host.WhenIdleAsync();

        Assert.Equal(["execute"], applied);
        Assert.False(host.TryScheduleAfterTurn("mode", _ => Task.CompletedTask));
    }

    [Fact]
    public async Task StartTurn_ExecutionTask_UsesExecutionScope()
    {
        await using var host = CreateHost();
        var scope = ExecutionTestScopes.Scope(ExecutionTestScopes.Context(userId: "owner"));
        ExecutionScope? capturedScope = null;
        string? capturedUser = null;

        var turn = host.StartTurn(
            scope,
            new CancellationTokenSource(),
            interruptAction: () => { },
            executeAsync: _ =>
            {
                capturedScope = ExecutionScope.Current;
                capturedUser = UserInfoUtil.UserId;
                return Task.CompletedTask;
            }
        );

        Assert.NotNull(turn);
        await host.WhenIdleAsync();
        Assert.Same(scope, capturedScope);
        Assert.Equal("owner", capturedUser);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public async Task DisposeAsync_ActiveTurn_InterruptsWaitsAndReleasesRuntime()
    {
        var runtime = AgentflowRuntimeFactory.CreateRuntime(
            Guid.CreateVersion7(),
            new AgentExecutionTask(),
            ExecutionSettings.CreateDefault(),
            deferHumanInteractions: false
        );
        var host = new InProcessTurnHost(
            new ExecutionTarget(runtime.AgentflowId, AgentRuntimeType.Agentflow),
            runtime,
            "fingerprint",
            permissionVersion: 0
        );
        var turnEnded = false;
        host.StartTurn(
            Scope(),
            new CancellationTokenSource(),
            () => { },
            async token =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    turnEnded = true;
                }
            }
        );

        await host.DisposeAsync();

        Assert.True(turnEnded);
        Assert.Throws<ObjectDisposedException>(() => host.TryScheduleAfterTurn("mode", _ => Task.CompletedTask));
    }

    private static InProcessTurnHost CreateHost()
    {
        var runtime = AgentflowRuntimeFactory.CreateRuntime(
            Guid.CreateVersion7(),
            new AgentExecutionTask(),
            ExecutionSettings.CreateDefault(),
            deferHumanInteractions: false
        );
        return new InProcessTurnHost(
            new ExecutionTarget(runtime.AgentflowId, AgentRuntimeType.Agentflow),
            runtime,
            "fingerprint",
            permissionVersion: 0
        );
    }

    private static ExecutionScope Scope() => ExecutionTestScopes.Scope();
}
