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
    public async Task UpsertAsync_WhenBindingExists_UpdatesProviderSessionId()
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
        var taskId = Guid.NewGuid();
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
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = new TaskSessionBindingService(
            new EfRepository<TaskSessionBinding>(dbContext),
            new UnitOfWork(dbContext));

        await service.UpsertAsync(
            taskId,
            agentId,
            "codex",
            "11111111-1111-1111-1111-111111111111",
            "tester",
            cancellationToken);
        await service.UpsertAsync(
            taskId,
            agentId,
            "codex",
            "22222222-2222-2222-2222-222222222222",
            "tester",
            cancellationToken);

        var bindings = await dbContext.Set<TaskSessionBinding>()
            .Where(binding => binding.TaskId == taskId)
            .ToListAsync(cancellationToken);
        var binding = Assert.Single(bindings);
        Assert.Equal(agentId, binding.AgentId);
        Assert.Equal("codex", binding.ExternalAgentName);
        Assert.Equal("22222222-2222-2222-2222-222222222222", binding.ProviderSessionId);
    }
}
