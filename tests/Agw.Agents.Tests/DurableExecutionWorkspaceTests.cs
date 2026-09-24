using Agw.Agents.Execution.Persistence.Durable;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed partial class DurableExecutionStoreTests
{
    [Fact]
    public async Task AcceptAsync_DirectoryConfigurationChanges_ResendKeepsOriginalSnapshot()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();
        var projects = new WorkspaceProjectFacade(task.ProjectId);
        var turnId = Guid.CreateVersion7();
        await AcceptAsync(task, turnId, projects: projects);
        var original = (await Store.GetAsync(turnId, token)).Manifest.WorkspaceSnapshot!;
        projects.Project = projects.Project with { AdditionalDirectories = [] };

        // Act
        var resend = await AcceptAsync(task, turnId, projects: projects);
        var next = await AcceptAsync(task, projects: projects);

        // Assert
        Assert.False(resend.Created);
        var retained = (await Store.GetAsync(turnId, token)).Manifest.WorkspaceSnapshot!;
        Assert.Equal(original.Fingerprint, retained.Fingerprint);
        Assert.Single(retained.AdditionalDirectories);
        var nextSnapshot = (await Store.GetAsync(next.Request.TurnId, token)).Manifest.WorkspaceSnapshot!;
        Assert.Empty(nextSnapshot.AdditionalDirectories);
        Assert.NotEqual(original.Fingerprint, nextSnapshot.Fingerprint);
    }

    [Theory]
    [InlineData("workspaceSnapshot")]
    [InlineData("userId")]
    public async Task GetAsync_MissingManifestField_RejectsWithoutCapturingOrPersisting(string field)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var accepted = await AcceptAsync(agentType: AgentRuntimeType.Agentflow);
        var id = accepted.Request.TurnId;
        var registered = await Store.GetAsync(id, token);
        var json = System.Text.Json.Nodes.JsonNode.Parse(DurableExecutionJson.Serialize(registered.Manifest))!;
        Assert.True(json.AsObject().Remove(field));
        var unsupportedJson = json.ToJsonString();
        await using (var context = _kit.CreateContext())
        {
            var record = await context.DurableExecutions.SingleAsync(item => item.Id == id, token);
            record.ManifestJson = unsupportedJson;
            await context.SaveChangesAsync(token);
        }

        // Act
        var exception = await Assert.ThrowsAsync<AgwException>(() => Store.GetAsync(id, token));

        // Assert
        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, exception.Code);
        Assert.Equal(unsupportedJson, (await _kit.ReadExecutionAsync(id)).ManifestJson);
    }

    private sealed class WorkspaceProjectFacade : IProjectRuntimeFacade
    {
        public ProjectRuntimeSnapshot Project { get; set; }

        public WorkspaceProjectFacade(Guid projectId)
        {
            Project = new ProjectRuntimeSnapshot(
                projectId,
                "project",
                AppContext.BaseDirectory,
                null,
                [],
                new Dictionary<string, string>(),
                [],
                [],
                [],
                [
                    new ProjectWorkspaceDirectory(
                        Guid.CreateVersion7(),
                        Path.Combine(AppContext.BaseDirectory, "additional")
                    ),
                ]
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
