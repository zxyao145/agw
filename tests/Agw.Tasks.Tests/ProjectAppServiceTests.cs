using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Tasks.Application;
using Agw.Tasks.Domain.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Tests;

public class ProjectAppServiceTests
{
    [Fact]
    public async Task CreateAsync_WhenWorkspaceDoesNotExist_CreatesWorkspaceDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tempRoot = Path.Combine(Path.GetTempPath(), "agw-project-tests", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(tempRoot, "workspace");

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);

            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;

            await using (var setupContext = new AgwDbContext(options))
            {
                await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            }

            await using var dbContext = new AgwDbContext(options);
            var service = CreateService(dbContext);

            var created = await service.CreateAsync(
                new Project
                {
                    Name = "Project A",
                    Type = ProjectType.UserDefined,
                    Workspace = workspace,
                    Enable = true
                },
                "tester");

            Assert.NotNull(created);
            Assert.True(Directory.Exists(workspace));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static ProjectAppService CreateService(AgwDbContext dbContext)
    {
        var projectRepository = new EfRepository<Project>(dbContext);

        return new ProjectAppService(
            projectRepository,
            new EfRepository<AgentflowTrace>(dbContext),
            new UnitOfWork(dbContext),
            new ProjectDomainService(TimeProvider.System),
            new ProjectResolver(projectRepository));
    }
}
