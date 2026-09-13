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

    [Fact]
    public async Task EnsureWorkspaceSnapshotAsync_LegacyManifest_CapturesOnlyPrimaryAndPersistsOnce()
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
        var legacy = registered.Manifest with { WorkspaceSnapshot = null };
        var record = await database.Context.DurableExecutions.SingleAsync(token);
        record.ManifestJson = DurableExecutionJson.Serialize(legacy);
        await database.Context.SaveChangesAsync(token);

        var recovered = await store.EnsureWorkspaceSnapshotAsync(legacy, token);
        Assert.Empty(recovered.WorkspaceSnapshot!.AdditionalDirectories);
        Assert.Equal(
            ProjectWorkspacePaths.Normalize(projects.Project.Workspace!),
            recovered.WorkspaceSnapshot.Workspace
        );
        projects.Project = projects.Project with { Workspace = Path.Combine(Path.GetTempPath(), "changed") };
        var repeated = await store.EnsureWorkspaceSnapshotAsync(legacy, token);
        Assert.Equal(recovered.WorkspaceSnapshot.Fingerprint, repeated.WorkspaceSnapshot!.Fingerprint);
        Assert.Equal(
            recovered.WorkspaceSnapshot.Fingerprint,
            (await store.GetAsync(legacy.ExecutionId, token)).Manifest.WorkspaceSnapshot!.Fingerprint
        );
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
