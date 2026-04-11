using System.Reflection;

using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Extensions;
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
            "ClearRecordsAsync",
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

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var projectId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
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

        await using var dbContext = new AgwDbContext(options);
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

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var projectId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
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

        await using var dbContext = new AgwDbContext(options);
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

    [Fact]
    public async Task ClearRecordsAsync_WhenTaskBelongsToProject_RemovesOnlyTaskRecords()
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
        var otherTaskId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Project",
                Type = ProjectType.UserDefined,
                Enable = true,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });

            seedContext.ProjectTasks.AddRange(
                new ProjectTask
                {
                    Id = taskId,
                    ProjectId = projectId,
                    ContextId = "context-1",
                    Title = "Task",
                    Status = ProjectTaskStatus.Running,
                    CreateBy = "tester",
                    CreateTime = DateTime.UtcNow
                },
                new ProjectTask
                {
                    Id = otherTaskId,
                    ProjectId = projectId,
                    ContextId = "context-2",
                    Title = "Other Task",
                    Status = ProjectTaskStatus.Running,
                    CreateBy = "tester",
                    CreateTime = DateTime.UtcNow
                });

            seedContext.TaskRecords.AddRange(
                new TaskRecord
                {
                    Id = Guid.NewGuid(),
                    TaskId = taskId,
                    ConversationSequence = 0,
                    ConversationPayload = "first",
                    CreateTime = DateTime.UtcNow
                },
                new TaskRecord
                {
                    Id = Guid.NewGuid(),
                    TaskId = taskId,
                    ConversationSequence = 1,
                    ConversationPayload = "second",
                    CreateTime = DateTime.UtcNow
                },
                new TaskRecord
                {
                    Id = Guid.NewGuid(),
                    TaskId = otherTaskId,
                    ConversationSequence = 0,
                    ConversationPayload = "other",
                    CreateTime = DateTime.UtcNow
                });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var result = await service.ClearRecordsAsync(projectId, taskId);

        Assert.Equal(ApplicationResultType.Success, result.Type);
        Assert.NotNull(await dbContext.ProjectTasks.FindAsync([taskId], cancellationToken));
        Assert.Empty(await dbContext.TaskRecords
            .Where(record => record.TaskId == taskId)
            .ToListAsync(cancellationToken));
        var remainingRecord = await dbContext.TaskRecords.SingleAsync(cancellationToken);
        Assert.Equal(otherTaskId, remainingRecord.TaskId);
    }

    [Fact]
    public async Task ClearRecordsAsync_WhenTaskDoesNotBelongToProject_ReturnsNotFoundAndKeepsRecords()
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
        var otherProjectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.AddRange(
                new Project
                {
                    Id = projectId,
                    Name = "Project",
                    Type = ProjectType.UserDefined,
                    Enable = true,
                    CreateBy = "tester",
                    CreateTime = DateTime.UtcNow
                },
                new Project
                {
                    Id = otherProjectId,
                    Name = "Other Project",
                    Type = ProjectType.UserDefined,
                    Enable = true,
                    CreateBy = "tester",
                    CreateTime = DateTime.UtcNow
                });

            seedContext.ProjectTasks.Add(new ProjectTask
            {
                Id = taskId,
                ProjectId = otherProjectId,
                ContextId = "context-1",
                Title = "Task",
                Status = ProjectTaskStatus.Running,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });

            seedContext.TaskRecords.Add(new TaskRecord
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                ConversationSequence = 0,
                ConversationPayload = "first",
                CreateTime = DateTime.UtcNow
            });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        var service = CreateService(dbContext);

        var result = await service.ClearRecordsAsync(projectId, taskId);

        Assert.Equal(ApplicationResultType.NotFound, result.Type);
        Assert.NotNull(await dbContext.ProjectTasks.FindAsync([taskId], cancellationToken));
        Assert.Single(await dbContext.TaskRecords
            .Where(record => record.TaskId == taskId)
            .ToListAsync(cancellationToken));
    }

    private static ProjectTaskAppService CreateService(AgwDbContext dbContext)
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
