using Agw.Domain.Services;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared;
using Agw.Shared.Contracts;
using Agw.Shared.Enums;
using Agw.Shared.Tasks.Entities;
using Agw.Tasks.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Tests;

public class ProjectTaskAppServiceTests
{
    [Fact]
    public async Task CreateRunningAsync_CreatesRunningTaskWithInitialRecord()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new LlmDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var projectId = Guid.NewGuid();
        await using (var seedContext = new LlmDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Jobs Project",
                Type = ProjectType.UserDefined,
                Enable = true,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new LlmDbContext(options);
        var service = CreateService(dbContext);
        var agentId = Guid.NewGuid();

        var result = await service.CreateRunningAsync(
            projectId,
            new ProjectTaskCreateRequest(
                AgentType: AgentRuntimeType.Agent,
                AgentflowId: null,
                AgentId: agentId,
                Description: "Nightly sync",
                Input: "Run scheduled sync",
                SessionId: "session-1",
                Title: "Nightly sync",
                SystemPrompt: null,
                ContextId: "context-1"),
            "job-executor");

        Assert.Equal(ApplicationResultType.Success, result.Type);
        var response = Assert.IsType<ProjectTaskResponse>(result.Value);
        Assert.Equal(ProjectTaskStatus.Running, response.Status);
        Assert.Equal(projectId.Normalize(), response.ProjectId);
        Assert.Equal("context-1", response.ContextId);
        Assert.Equal("session-1", response.SessionId);
        Assert.Equal("Nightly sync", response.Title);
        Assert.Equal("Nightly sync", response.Description);
        Assert.Equal("Run scheduled sync", response.Input);
        Assert.NotNull(response.StartedTime);

        var task = await dbContext.ProjectTasks.SingleAsync(
            x => x.Id == response.Id,
            cancellationToken);
        Assert.Equal(ProjectTaskStatus.Running, task.Status);
        Assert.Equal("job-executor", task.CreateBy);
        Assert.Equal("job-executor", task.UpdateBy);

        var record = await dbContext.TaskRecords.SingleAsync(
            x => x.ContextId == "context-1",
            cancellationToken);
        Assert.Equal("session-1", record.SessionId);
        Assert.Equal(0, record.ConversationSequence);
        Assert.Equal("Run scheduled sync", record.GetText());
    }

    private static ProjectTaskAppService CreateService(LlmDbContext dbContext)
    {
        var projectRepository = new EfRepository<Project>(dbContext);

        return new ProjectTaskAppService(
            new EfRepository<ProjectTask>(dbContext),
            new EfRepository<TaskRecord>(dbContext),
            new UnitOfWork(dbContext),
            new ProjectTaskDomainService(),
            new TaskRecordDomainService(),
            new ProjectResolver(projectRepository));
    }
}
