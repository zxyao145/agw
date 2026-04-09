using System.Reflection;

using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Enums;
using Agw.Tasks.Application;
using Agw.Tasks.Domain.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Tests;

public class ProjectTaskAppServiceTests
{
    [Fact]
    public void ProjectTaskCreateRequest_RemovesLegacyTargetBindingConstructor()
    {
        Assert.DoesNotContain(
            typeof(ProjectTaskCreateRequest).GetConstructors(),
            constructor =>
            {
                var parameters = constructor.GetParameters();
                return parameters.Length == 7
                    && parameters[0].Name == "AgentType"
                    && parameters[1].Name == "AgentflowId"
                    && parameters[2].Name == "AgentId"
                    && parameters[3].Name == "Description";
            });
    }

    [Fact]
    public void ProjectTaskAppService_RemovesGetNextPendingAsync()
    {
        Assert.Null(typeof(ProjectTaskAppService).GetMethod("GetNextPendingAsync"));
    }

    [Fact]
    public void ProjectTaskAppService_ExposesOnlyCurrentPublicSurface()
    {
        string[] expectedMethods =
        [
            "CreateAsync",
            "CreateForExecutionAsync",
            "CreateRunningAsync",
            "DeleteAsync",
            "GetLatestRecordAsync",
            "GetResponseAsync",
            "GetTaskAsync",
            "ListAsync",
            "ListResponsesAsync",
            "MarkFailedAsync",
            "MarkSucceededAsync",
            "UpdateTitleAsync"
        ];

        var methodNames = typeof(ProjectTaskAppService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(expectedMethods, methodNames);
    }

    [Fact]
    public async Task CreateRunningAsync_PersistsJobIdAndReturnsTitleOnlySummary()
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
        var jobId = Guid.NewGuid();

        var result = await service.CreateRunningAsync(
            projectId,
            new ProjectTaskCreateRequest(
                JobId: jobId,
                Input: "Run scheduled sync",
                Title: "Nightly sync",
                ContextId: "context-1"),
            "job-executor");

        Assert.Equal(ApplicationResultType.Success, result.Type);
        var response = Assert.IsType<ProjectTaskResponse>(result.Value);
        Assert.Equal(jobId, response.JobId);
        Assert.Equal(ProjectTaskStatus.Running, response.Status);
        Assert.Equal(projectId.Normalize(), response.ProjectId);
        Assert.Equal("context-1", response.ContextId);
        Assert.Equal("Nightly sync", response.Title);
        Assert.Equal("Run scheduled sync", response.Input);
        Assert.Null(typeof(ProjectTaskResponse).GetProperty("Description"));
        Assert.Null(typeof(ProjectTaskResponse).GetProperty("AgentType"));
        Assert.NotNull(response.StartedTime);

        var task = await dbContext.ProjectTasks.SingleAsync(
            x => x.Id == response.Id,
            cancellationToken);
        Assert.Equal(ProjectTaskStatus.Running, task.Status);
        Assert.Equal("job-executor", task.CreateBy);
        Assert.Equal("job-executor", task.UpdateBy);

        var record = await dbContext.TaskRecords.SingleAsync(
            x => x.TaskId == response.Id,
            cancellationToken);
        Assert.Equal(response.Id, record.TaskId);
        Assert.Equal(0, record.ConversationSequence);
        Assert.Equal("Run scheduled sync", record.GetText());
    }

    [Fact]
    public async Task ListResponsesAsync_ReturnsTaskOnlySummaryWithoutTaskRecords()
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

            seedContext.ProjectTasks.Add(new ProjectTask
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                ContextId = "context-1",
                JobId = Guid.NewGuid(),
                Title = "Nightly sync",
                Status = ProjectTaskStatus.Succeeded,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow,
                UpdateBy = "tester",
                UpdateTime = DateTime.UtcNow,
                FinishedTime = DateTime.UtcNow
            });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new LlmDbContext(options);
        var service = CreateService(dbContext);

        var responses = await service.ListResponsesAsync(projectId);

        var response = Assert.Single(responses);
        Assert.IsType<ProjectTaskSummaryResponse>(response);
        Assert.Null(typeof(ProjectTaskSummaryResponse).GetProperty("Messages"));
        Assert.NotNull(typeof(ProjectTaskSummaryResponse).GetProperty("Id"));
        Assert.Null(typeof(ProjectTaskSummaryResponse).GetProperty("Input"));
        Assert.Null(typeof(ProjectTaskSummaryResponse).GetProperty("MessageCount"));
        Assert.Null(typeof(ProjectTaskSummaryResponse).GetProperty("Description"));
        Assert.Null(typeof(ProjectTaskSummaryResponse).GetProperty("AgentType"));
        Assert.Equal(ProjectTaskStatus.Succeeded, response.Status);
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
