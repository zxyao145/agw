using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Turns;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;

namespace Agw.Agents.Tests;

public class RuntimeTurnContextAccessorTests
{
    [Fact]
    public void Push_WhenScopeEnds_RestoresPreviousContext()
    {
        var accessor = new RuntimeTurnContextAccessor();
        var outer = CreateContext("outer");
        var inner = CreateContext("inner");

        using (accessor.Push(outer))
        {
            Assert.Same(outer, accessor.Current);
            using (accessor.Push(inner))
            {
                Assert.Same(inner, accessor.Current);
            }

            Assert.Same(outer, accessor.Current);
        }

        Assert.Null(accessor.Current);
    }

    [Fact]
    public async Task Push_ConcurrentFlows_IsolatesContexts()
    {
        var accessor = new RuntimeTurnContextAccessor();
        var first = CreateContext("first");
        var second = CreateContext("second");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<RuntimeTurnContext?> CaptureAsync(RuntimeTurnContext context)
        {
            using var scope = accessor.Push(context);
            await release.Task;
            return accessor.Current;
        }

        var firstTask = Task.Run(() => CaptureAsync(first));
        var secondTask = Task.Run(() => CaptureAsync(second));
        release.SetResult();

        Assert.Same(first, await firstTask);
        Assert.Same(second, await secondTask);
        Assert.Null(accessor.Current);
    }

    private static RuntimeTurnContext CreateContext(string userName)
    {
        var projectId = Guid.CreateVersion7();
        return new RuntimeTurnContext(
            ExecutionSettings.FromCommand(new SettingCommand(projectId)),
            new TaskProjection
            {
                TaskId = Guid.CreateVersion7(),
                ProjectId = projectId,
                ContextId = "context",
                CreateTime = TimeProvider.System.GetUtcNow(),
            },
            new ExecutionTarget(Guid.CreateVersion7(), AgentRuntimeType.Agent),
            userName,
            "/workspace",
            new NullSink());
    }

    private sealed class NullSink : IExecutionMessageSink
    {
        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
