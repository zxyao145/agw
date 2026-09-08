using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Tests;

public sealed partial class DurableExecutionStoreTests
{
    [Theory]
    [InlineData(AgentRuntimeType.Agent)]
    [InlineData(AgentRuntimeType.Agentflow)]
    public async Task StartAsync_Durable_AcceptsQueuedExecutionAndPreservesItAfterDetach(AgentRuntimeType agentType)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        await using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var coordinator = CreateStarterCoordinator(services);
        var task = CreateTask(database);
        var request = new ExecutionStartRequest(
            Guid.CreateVersion7(),
            new ExecutionTarget(Guid.CreateVersion7(), agentType),
            task,
            CreateSettings(task.ProjectId, task.ContextId),
            CreateInput("start"),
            true,
            "/workspace"
        );

        var receipts = new List<(ExecutionReceipt Receipt, Guid? ActiveId)>();

        // Act: retry the same request on another connection, with no Worker running.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var session = new DurableExecutionSession(
                "user-id",
                new StarterMessageSink(),
                token,
                coordinator
            );
            IExecutionStarter starter = new DurableExecutionStarter(session);
            var receipt = await starter.StartAsync(request, token).WaitAsync(TimeSpan.FromSeconds(5), token);

            receipts.Add((receipt, session.ActiveExecutionId));
        }

        // Assert
        Assert.All(
            receipts,
            entry =>
            {
                Assert.True(entry.Receipt.Accepted);
                Assert.Equal(request.ExecutionId, entry.Receipt.ExecutionId);
                Assert.Equal(request.ExecutionId, entry.ActiveId);
            }
        );
        var snapshot = await store.GetAsync(request.ExecutionId, token);
        Assert.Equal(DurableExecutionStatus.Queued, snapshot.Status);
        Assert.Equal(agentType, snapshot.Manifest.AgentType);
        Assert.Equal(request.Target.AgentId, snapshot.Manifest.AgentId);
        Assert.Equal(task.ProjectConversationId, snapshot.Manifest.Task.ProjectConversationId);
        Assert.Equal("user-id", snapshot.Manifest.ResolveUserId());
        Assert.Single(await database.Context.DurableExecutions.ToListAsync(token));
    }

    [Fact]
    public async Task StartAsync_DurableNonStreaming_RejectsWithoutRegisteringExecution()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync();
        await using var services = new ServiceCollection().AddSingleton(database.CreateStore()).BuildServiceProvider();
        await using var session = new DurableExecutionSession(
            "user-id",
            new StarterMessageSink(),
            token,
            CreateStarterCoordinator(services)
        );
        IExecutionStarter starter = new DurableExecutionStarter(session);
        var task = CreateTask(database);
        var request = new ExecutionStartRequest(
            Guid.CreateVersion7(),
            new ExecutionTarget(Guid.CreateVersion7(), AgentRuntimeType.Agent),
            task,
            CreateSettings(task.ProjectId, task.ContextId),
            CreateInput("start"),
            false,
            "/workspace"
        );

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() => starter.StartAsync(request, token));

        // Assert
        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Null(session.ActiveExecutionId);
        Assert.Empty(await database.Context.DurableExecutions.ToListAsync(token));
    }

    private static DurableExecutionCoordinator CreateStarterCoordinator(IServiceProvider services) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            new InMemoryApplicationLock(),
            new RecordingExecutionEventStream(),
            TimeProvider.System,
            Options.Create(new ExecutionRuntimeOptions()),
            NullLogger<DurableExecutionCoordinator>.Instance
        );

    private sealed class StarterMessageSink : IExecutionMessageSink
    {
        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
