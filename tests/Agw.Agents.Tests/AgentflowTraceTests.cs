using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AgentflowExecutionTraceTestCollection
{
    public const string Name = "Agentflow execution trace";
}

[Collection(AgentflowExecutionTraceTestCollection.Name)]
public class AgentflowTraceTests
{
    [Fact]
    public async Task ActivityCompleted_QueuesTraceWithoutPuttingInputInTags()
    {
        var store = new CapturingTraceStore();
        using var collector = new AgentflowNodeExecutionTraceCollector(
            store,
            NullLogger<AgentflowNodeExecutionTraceCollector>.Instance
        );
        await collector.StartAsync(TestContext.Current.CancellationToken);

        var execution = new AgentflowExecutionTraceContext(Guid.CreateVersion7(), "context-1", Guid.CreateVersion7());
        var agentflowId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        using (
            var scope = AgentflowNodeExecutionActivity.StartAgent(
                execution,
                agentflowId,
                "node-1",
                "Node Alias",
                agentId,
                "persisted-agent",
                [new ChatMessage(ChatRole.User, "hello")]
            )
        )
        {
            Assert.NotNull(scope.Activity);
            Assert.Equal("Agw.Agentflow.Execution.Persistence", scope.Activity.Source.Name);
            Assert.DoesNotContain(
                scope.Activity.TagObjects,
                tag => tag.Key.Contains("input", StringComparison.OrdinalIgnoreCase)
            );
            scope.Complete();
        }

        var trace = await store.WaitForTraceAsync();
        Assert.Equal(execution.ProjectId, trace.ProjectId);
        Assert.Equal(execution.ContextId, trace.ContextId);
        Assert.Equal(execution.TaskId, trace.TaskId);
        Assert.Equal(agentflowId, trace.AgentflowId);
        Assert.Equal("node-1", trace.NodeId);
        Assert.Equal("Node Alias", trace.NodeName);
        Assert.Equal(AgentflowNodeKind.Agent, trace.NodeKind);
        Assert.Equal(agentId, trace.AgentId);
        Assert.Equal("persisted-agent", trace.AgentName);
        Assert.Contains("hello", trace.Input, StringComparison.Ordinal);
        Assert.Equal(AgentflowNodeExecutionStatus.Succeeded, trace.Status);
        Assert.Null(trace.Error);
        Assert.True(trace.StartTimeUtc > DateTimeOffset.UnixEpoch);
        Assert.True(trace.DurationMilliseconds >= 0);

        await collector.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ActivityFailed_QueuesFailureAndDoesNotThrowWhenStoreFails()
    {
        var store = new ThrowingTraceStore();
        using var collector = new AgentflowNodeExecutionTraceCollector(
            store,
            NullLogger<AgentflowNodeExecutionTraceCollector>.Instance
        );
        await collector.StartAsync(TestContext.Current.CancellationToken);

        using (
            var scope = AgentflowNodeExecutionActivity.StartHumanGate(
                new AgentflowExecutionTraceContext(Guid.CreateVersion7(), "context-2", Guid.CreateVersion7()),
                Guid.CreateVersion7(),
                "human",
                "Approval",
                [new ChatMessage(ChatRole.User, "review")]
            )
        )
        {
            scope.Fail(new InvalidOperationException("approval unavailable"));
        }

        await store.WriteAttempted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await collector.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HumanGateRejected_QueuesRejectedStatusWithoutAgentOrOutput()
    {
        var store = new CapturingTraceStore();
        using var collector = new AgentflowNodeExecutionTraceCollector(
            store,
            NullLogger<AgentflowNodeExecutionTraceCollector>.Instance
        );
        await collector.StartAsync(TestContext.Current.CancellationToken);

        using (
            var scope = AgentflowNodeExecutionActivity.StartHumanGate(
                new AgentflowExecutionTraceContext(Guid.CreateVersion7(), "context-3", Guid.CreateVersion7()),
                Guid.CreateVersion7(),
                "human",
                "Approval",
                [new ChatMessage(ChatRole.User, "review")]
            )
        )
        {
            scope.Reject();
        }

        var trace = await store.WaitForTraceAsync();
        Assert.Equal(AgentflowNodeExecutionStatus.Rejected, trace.Status);
        Assert.Null(trace.AgentId);
        Assert.Null(trace.AgentName);
        Assert.Null(trace.Error);
        Assert.Null(typeof(AgentflowTrace).GetProperty("Output"));

        await collector.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class CapturingTraceStore : IAgentflowNodeExecutionTraceStore
    {
        private readonly TaskCompletionSource<AgentflowTrace> _trace = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task SaveAsync(AgentflowTrace trace, CancellationToken cancellationToken)
        {
            _trace.TrySetResult(trace);
            return Task.CompletedTask;
        }

        public Task<AgentflowTrace> WaitForTraceAsync() => _trace.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private sealed class ThrowingTraceStore : IAgentflowNodeExecutionTraceStore
    {
        public TaskCompletionSource WriteAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SaveAsync(AgentflowTrace trace, CancellationToken cancellationToken)
        {
            WriteAttempted.TrySetResult();
            throw new InvalidOperationException("database unavailable");
        }
    }
}
