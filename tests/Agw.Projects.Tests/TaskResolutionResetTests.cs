using Agw.Projects.Application.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public partial class TaskAppServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveTaskAsync_SameScopeAfterReset_UsesNewGeneration(bool resume)
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(token);
        var projectId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        await database.SeedAsync(
            token,
            CreateProject(projectId),
            CreateConversation(conversationId, projectId, "reset")
        );
        await using var connectionScope = database.CreateContext();
        var tasks = CreateService(connectionScope);
        var request = new ExecutionTaskRequest(null, conversationId, projectId, "reset", "old", false, "tester");
        var old = await tasks.ResolveTaskAsync(request, token);
        Assert.NotNull(old.Task);
        Assert.Equal(0, old.Task.Generation);

        await using (var controlPlane = database.CreateContext())
        {
            await TestProjectPersistence
                .CreateDeletionCoordinator(controlPlane)
                .ClearConversationRecordsAsync(
                    new ProjectConversationDeletionTarget(projectId, conversationId, "reset", "tester"),
                    token
                );
        }
        var next = await tasks.ResolveTaskAsync(request with { Input = "new", Resume = resume }, token);

        Assert.Null(next.Error);
        Assert.NotNull(next.Task);
        Assert.Equal(1, next.Task.Generation);
        Assert.NotEqual(old.Task.TaskId, next.Task.TaskId);
        Assert.Equal(1, await connectionScope.ProjectConversationChatHistories.CountAsync(token));
    }
}
