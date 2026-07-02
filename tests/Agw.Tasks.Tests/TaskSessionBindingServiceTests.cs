using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Tasks.Application;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Tests;

public class TaskSessionBindingServiceTests
{
    [Fact]
    public async Task UpsertAsync_WhenContextBindingExists_UpdatesProviderSessionId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

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

        var projectId = Guid.NewGuid();
        var projectContextId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Binding Project",
                Type = ProjectType.UserDefined,
                Enable = true,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });
            seedContext.ProjectContexts.Add(new ProjectContext
            {
                Id = projectContextId,
                ProjectId = projectId,
                ContextId = "context-1",
                Title = "Binding Context",
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = new TaskSessionBindingService(
            new EfRepository<TaskSessionBinding>(dbContext),
            new EfRepository<ProjectContext>(dbContext),
            new UnitOfWork(dbContext));

        await service.UpsertAsync(
            projectId,
            "context-1",
            agentId,
            "codex",
            "11111111-1111-1111-1111-111111111111",
            "tester",
            cancellationToken);
        await service.UpsertAsync(
            projectId,
            "context-1",
            agentId,
            "codex",
            "22222222-2222-2222-2222-222222222222",
            "tester",
            cancellationToken);

        var bindings = await dbContext.Set<TaskSessionBinding>()
            .Where(binding => binding.ProjectContextId == projectContextId)
            .ToListAsync(cancellationToken);
        var binding = Assert.Single(bindings);
        Assert.Equal(projectContextId, binding.ProjectContextId);
        Assert.Equal(agentId, binding.AgentId);
        Assert.Equal("codex", binding.ExternalAgentName);
        Assert.Equal("22222222-2222-2222-2222-222222222222", binding.ProviderSessionId);
    }

    [Fact]
    public async Task GetAsync_WhenDifferentTasksUseSameContext_ReturnsSameContextBinding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

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

        var projectId = Guid.NewGuid();
        var projectContextId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Binding Project",
                Type = ProjectType.UserDefined,
                Enable = true,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });
            seedContext.ProjectContexts.Add(new ProjectContext
            {
                Id = projectContextId,
                ProjectId = projectId,
                ContextId = "context-1",
                Title = "Binding Context",
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = new TaskSessionBindingService(
            new EfRepository<TaskSessionBinding>(dbContext),
            new EfRepository<ProjectContext>(dbContext),
            new UnitOfWork(dbContext));

        await service.UpsertAsync(
            projectId,
            "context-1",
            agentId,
            "codex",
            "11111111-1111-1111-1111-111111111111",
            "tester",
            cancellationToken);

        var binding = await service.GetAsync(projectId, "context-1", agentId, "CODEX", cancellationToken);

        Assert.NotNull(binding);
        Assert.Equal(projectContextId, binding!.ProjectContextId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", binding.ProviderSessionId);
    }

    [Fact]
    public async Task UpsertAsync_WhenSameBindingCreatedConcurrently_CompletesWithSingleBinding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dbPath = Path.Combine(Path.GetTempPath(), $"agw-binding-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .UseSnakeCaseNamingConvention()
                .Options;

            await using (var setupContext = new AgwDbContext(options))
            {
                await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            }

            var projectId = Guid.NewGuid();
            var projectContextId = Guid.NewGuid();
            var agentId = Guid.NewGuid();
            await using (var seedContext = new AgwDbContext(options))
            {
                seedContext.Projects.Add(new Project
                {
                    Id = projectId,
                    Name = "Binding Project",
                    Type = ProjectType.UserDefined,
                    Enable = true,
                    CreateBy = "tester",
                    CreateTime = DateTime.UtcNow
                });
                seedContext.ProjectContexts.Add(new ProjectContext
                {
                    Id = projectContextId,
                    ProjectId = projectId,
                    ContextId = "context-1",
                    Title = "Binding Context",
                    CreateBy = "tester",
                    CreateTime = DateTime.UtcNow
                });
                await seedContext.SaveChangesAsync(cancellationToken);
            }

            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var upserts = Enumerable.Range(0, 12)
                .Select(async index =>
                {
                    await startGate.Task.WaitAsync(cancellationToken);
                    await using var dbContext = new AgwDbContext(options);
                    var service = new TaskSessionBindingService(
                        new EfRepository<TaskSessionBinding>(dbContext),
                        new EfRepository<ProjectContext>(dbContext),
                        new UnitOfWork(dbContext));

                    return await service.UpsertAsync(
                        projectId,
                        "context-1",
                        agentId,
                        "codex",
                        Guid.NewGuid().ToString("D"),
                        $"tester-{index}",
                        cancellationToken);
                })
                .ToArray();

            startGate.SetResult();
            await Task.WhenAll(upserts);

            await using var verifyContext = new AgwDbContext(options);
            var bindings = await verifyContext.TaskSessionBindings
                .Where(binding => binding.ProjectContextId == projectContextId
                    && binding.AgentId == agentId
                    && binding.ExternalAgentName == "codex")
                .ToListAsync(cancellationToken);

            Assert.Single(bindings);
            Assert.Contains(
                bindings[0].ProviderSessionId,
                upserts.Select(task => task.Result.ProviderSessionId));
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }
}
