using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Agents.Execution.Persistence.Durable;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public sealed partial class TurnAcceptanceTests
{
    [Fact]
    public async Task AppendAsync_MixedMessages_StoresMessageTypes()
    {
        var task = await _kit.SeedConversationAsync();
        var accepted = await AcceptAsync(task, durable: _kit.Coordinator);

        await AppendMixedEventsAsync(accepted.Request.TurnId);

        var records = await _kit.ReadEventsAsync(accepted.Request.TurnId);
        Assert.Equal(
            [AgwMessageTypes.TurnStart, AgwMessageTypes.AgentflowNodeStatus, null, AgwMessageTypes.AgentflowNodeStatus],
            records.Select(record => record.MessageType)
        );
    }

    [Fact]
    public async Task ReadByTypesAsync_MixedEvents_ReturnsRequestedTypesInSequence()
    {
        var task = await _kit.SeedConversationAsync();
        var accepted = await AcceptAsync(task, durable: _kit.Coordinator);
        var turnId = accepted.Request.TurnId;
        await AppendMixedEventsAsync(turnId);
        await using var scope = _kit.Services.CreateAsyncScope();

        var events = await DurableExecutionEvents.ReadByTypesAsync(
            scope.ServiceProvider.GetRequiredService<IAgentsDbContext>(),
            turnId,
            [AgwMessageTypes.TurnStart, AgwMessageTypes.AgentflowNodeStatus],
            TestContext.Current.CancellationToken
        );

        Assert.Equal([1L, 2L, 4L], events.Select(entry => entry.Sequence));
    }

    private async Task AppendMixedEventsAsync(Guid turnId)
    {
        await using var scope = _kit.Services.CreateAsyncScope();
        var running = AgentflowMessageMapper.CreateNodeStatusMessage(
            "input",
            AgwAgentflowNodeStatus.Running,
            null,
            turnId.ToString("D")
        );
        var completed = AgentflowMessageMapper.CreateNodeStatusMessage(
            "input",
            AgwAgentflowNodeStatus.Completed,
            null,
            turnId.ToString("D")
        );
        var text = new AgwMessage(
            Guid.CreateVersion7().ToString("D"),
            "assistant",
            AiRole.Assistant,
            [new AgwTextContent { Content = "text" }]
        );

        await DurableExecutionEvents.AppendAsync(
            scope.ServiceProvider.GetRequiredService<IAgentsDbContext>(),
            scope.ServiceProvider.GetRequiredService<IDurableExecutionEventSequence>(),
            turnId,
            leaseEpoch: 0,
            segmentIndex: 0,
            [
                PendingExecutionEvent.Create(running),
                PendingExecutionEvent.Create(text),
                PendingExecutionEvent.Create(completed),
            ],
            lookupCommitted: false,
            TestContext.Current.CancellationToken
        );
    }
}
