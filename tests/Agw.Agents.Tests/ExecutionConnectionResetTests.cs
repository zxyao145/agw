namespace Agw.Agents.Tests;

public partial class ExecutionCommandHandlerTests
{
    [Fact]
    public async Task StartTurnAsync_ConversationReset_DisposesCachedRuntimeAndResolvesNewTask()
    {
        var token = TestContext.Current.CancellationToken;
        var task = CreateTask("reset-context");
        var tasks = new FakeProjectTaskFacade(task);
        var factory = new FakeRuntimeFactory();
        await using var connection = CreateContext(factory, task, projectTasks: tasks);
        var command = CreateExecCommand(Guid.NewGuid());
        await connection.StartTurnAsync(command, token);
        var oldRuntime = Assert.Single(factory.CreatedRuntimes);

        tasks.Generation = 1; // A reset committed on another Control/Data Plane process.
        await connection.StartTurnAsync(command, token);

        Assert.True(oldRuntime.Disposed);
        Assert.Equal(2, tasks.ResolveCount);
        Assert.Equal(2, factory.CreatedRuntimes.Count);
        Assert.Null(factory.StartRequests[1].CurrentRuntime);
        Assert.Equal(1, factory.StartRequests[1].Task.Generation);
    }
}
