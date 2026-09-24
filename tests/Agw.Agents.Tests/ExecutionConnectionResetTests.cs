namespace Agw.Agents.Tests;

public partial class ExecutionCommandHandlerTests
{
    [Fact]
    public async Task StartTurnAsync_NewConversationWithoutStoredGeneration_ResolvesAndStartsRuntime()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var task = CreateTask("new-context");
        var tasks = new FakeProjectTaskFacade(task, _persistence) { Generation = null };
        await using var connection = CreateContext(task, projectTasks: tasks);
        var command = CreateExecCommand(Guid.NewGuid());

        // Act
        await connection.StartTurnAsync(command, token);
        await connection.WhenIdleAsync();

        // Assert
        Assert.Equal(1, tasks.ResolveCount);
        var execution = Assert.Single(Runtimes.TurnContexts);
        Assert.Equal(command.ConversationId, execution.ProjectConversationId);
        Assert.Equal(0, execution.Generation);
    }

    [Fact]
    public async Task StartTurnAsync_CachedConversationRemoved_RejectsAndDisposesRuntime()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var task = CreateTask("deleted-context");
        var tasks = new FakeProjectTaskFacade(task, _persistence);
        await using var connection = CreateContext(task, projectTasks: tasks);
        var command = CreateExecCommand(Guid.NewGuid());
        await connection.StartTurnAsync(command, token);
        await connection.WhenIdleAsync();
        var oldRuntime = Assert.Single(Runtimes.Created);
        tasks.Generation = null;

        // Act
        var exception = await Assert.ThrowsAsync<Agw.Shared.Exceptions.AgwException>(() =>
            connection.StartTurnAsync(command, token)
        );

        // Assert
        Assert.Equal(Agw.Shared.Exceptions.ErrorCodes.ResourceNotFound.Code, exception.Code);
        Assert.True(oldRuntime.IsDisposed);
        Assert.Null(connection.ResolvedTask);
        Assert.Equal(1, tasks.ResolveCount);
        Assert.Single(Runtimes.TurnContexts);
    }

    [Fact]
    public async Task StartTurnAsync_ConversationReset_DisposesCachedRuntimeAndResolvesNewTask()
    {
        var token = TestContext.Current.CancellationToken;
        var task = CreateTask("reset-context");
        var tasks = new FakeProjectTaskFacade(task, _persistence);
        await using var connection = CreateContext(task, projectTasks: tasks);
        var command = CreateExecCommand(Guid.NewGuid());
        await connection.StartTurnAsync(command, token);
        await connection.WhenIdleAsync();
        var oldRuntime = Assert.Single(Runtimes.Created);

        tasks.Generation = 1; // A reset committed on another Control/Data Plane process.
        await connection.StartTurnAsync(CreateNextCommand(command), token);
        await connection.WhenIdleAsync();

        Assert.True(oldRuntime.IsDisposed);
        Assert.Equal(2, tasks.ResolveCount);
        Assert.Equal(2, Runtimes.Created.Count);
        Assert.Equal(1, Runtimes.TurnContexts[1].Generation);
    }
}
