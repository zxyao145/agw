using Agw.Agents.Execution.Persistence.Durable;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Coordination;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed partial class DurableExecutionStoreTests
{
    [Fact]
    public async Task RegisterAsync_DirectoryConfigurationChanges_RetryKeepsOriginalSnapshot()
    {
        await using var database = await TestDatabase.CreateAsync();
        var task = CreateTask(database);
        var projects = new WorkspaceProjectFacade(task.ProjectId);
        var store = new DurableExecutionStore(
            database.Context,
            TimeProvider.System,
            InMemoryApplicationLock.Shared,
            TestDurablePersistence.Create(database.Context),
            projects
        );
        var executionId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        var token = TestContext.Current.CancellationToken;
        var first = await store.RegisterAsync(
            executionId,
            "user-id",
            agentId,
            AgentRuntimeType.Agent,
            CreateInput("hello"),
            task,
            CreateSettings(task.ProjectId, task.ContextId),
            token
        );
        var original = first.Manifest.WorkspaceSnapshot!;
        projects.Project = projects.Project with { AdditionalDirectories = [] };
        using var nextTurn = ProjectWorkspaceContext.Push(
            task.ProjectId,
            ProjectWorkspacePaths.CreateSnapshot(task.ProjectId, projects.Project.Workspace)
        );

        var retry = await store.RegisterAsync(
            executionId,
            "user-id",
            agentId,
            AgentRuntimeType.Agent,
            CreateInput("hello"),
            task,
            CreateSettings(task.ProjectId, task.ContextId),
            token
        );
        Assert.Equal(original.Fingerprint, retry.Manifest.WorkspaceSnapshot!.Fingerprint);
        Assert.Single(retry.Manifest.WorkspaceSnapshot.AdditionalDirectories);
        var next = await store.RegisterAsync(
            Guid.CreateVersion7(),
            "user-id",
            agentId,
            AgentRuntimeType.Agent,
            CreateInput("hello"),
            task,
            CreateSettings(task.ProjectId, task.ContextId),
            token
        );
        Assert.Empty(next.Manifest.WorkspaceSnapshot!.AdditionalDirectories);
        Assert.NotEqual(original.Fingerprint, next.Manifest.WorkspaceSnapshot.Fingerprint);
    }

    [Theory]
    [InlineData("workspaceSnapshot")]
    [InlineData("userId")]
    public async Task GetAsync_MissingManifestField_RejectsWithoutCapturingOrPersisting(string field)
    {
        await using var database = await TestDatabase.CreateAsync();
        var task = CreateTask(database);
        var projects = new WorkspaceProjectFacade(task.ProjectId);
        var store = new DurableExecutionStore(
            database.Context,
            TimeProvider.System,
            InMemoryApplicationLock.Shared,
            TestDurablePersistence.Create(database.Context),
            projects
        );
        var token = TestContext.Current.CancellationToken;
        var registered = await store.RegisterAsync(
            Guid.CreateVersion7(),
            "user-id",
            Guid.CreateVersion7(),
            AgentRuntimeType.Agentflow,
            CreateInput("hello"),
            task,
            CreateSettings(task.ProjectId, task.ContextId),
            token
        );
        var json = System.Text.Json.Nodes.JsonNode.Parse(DurableExecutionJson.Serialize(registered.Manifest))!;
        Assert.True(json.AsObject().Remove(field));
        var unsupportedJson = json.ToJsonString();
        var record = await database.Context.DurableExecutions.SingleAsync(token);
        record.ManifestJson = unsupportedJson;
        await database.Context.SaveChangesAsync(token);

        var exception = await Assert.ThrowsAsync<Agw.Shared.Exceptions.AgwException>(() =>
            store.GetAsync(registered.Manifest.ExecutionId, token)
        );
        Assert.Equal(Agw.Shared.Exceptions.ErrorCodes.DurableExecutionConflict.Code, exception.Code);
        database.Context.ChangeTracker.Clear();
        var unchanged = await database.Context.DurableExecutions.SingleAsync(token);
        Assert.Equal(unsupportedJson, unchanged.ManifestJson);
    }

    private sealed class WorkspaceProjectFacade : IProjectRuntimeFacade
    {
        public ProjectRuntimeSnapshot Project { get; set; }

        public WorkspaceProjectFacade(Guid projectId)
        {
            Project = new ProjectRuntimeSnapshot(
                projectId,
                "project",
                Path.GetTempPath(),
                null,
                [],
                new Dictionary<string, string>(),
                [],
                [],
                [],
                [new ProjectWorkspaceDirectory(Guid.CreateVersion7(), Path.Combine(Path.GetTempPath(), "additional"))]
            );
        }

        public Task<ProjectRuntimeSnapshot?> GetForCurrentUserAsync(
            Guid projectId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<ProjectRuntimeSnapshot?>(Project);

        public Task<string?> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Project.Workspace);
    }
}
