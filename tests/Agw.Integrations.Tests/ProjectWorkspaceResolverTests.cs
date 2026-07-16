using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Integrations.Tools.GitHub;
using Agw.Shared.Data.Entities.Projects;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Tests;

public sealed class ProjectWorkspaceResolverTests
{
    [Fact]
    public async Task ResolveWorkspaceAsync_ProjectExists_ReturnsPersistedWorkspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        var projectId = Guid.CreateVersion7();
        dbContext.Projects.Add(new Project
        {
            Id = projectId,
            Name = "workspace-project",
            Workspace = "~/.agw/workspace-project",
            CreateBy = "test",
            CreateTime = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        var resolver = new ProjectWorkspaceResolver(new EfRepository<Project>(dbContext));

        var workspace = await resolver.ResolveWorkspaceAsync(projectId, cancellationToken);

        Assert.Equal("~/.agw/workspace-project", workspace);
    }
}
