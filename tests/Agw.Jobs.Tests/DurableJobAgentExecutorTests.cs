using Agw.Agents.Execution.Durable;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Jobs.Execution;
using Agw.Projects.Application;
using Agw.Projects.Domain.Services;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Tests;

public sealed class DurableJobAgentExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_WaitsOnOutcomeWithoutReadingEventStream()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Name = "Job project",
            Type = ProjectType.UserDefined,
            CreateBy = "owner",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };
        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync(cancellationToken);

        var contextRepository = new EfRepository<ProjectConversation>(dbContext);
        var historyRepository = new EfRepository<ProjectConversationChatHistory>(dbContext);
        var taskExecution = new TaskExecutionAppService(
            contextRepository,
            historyRepository,
            dbContext,
            new ProjectConversationChatHistoryDomainService(),
            new ProjectResolver(new EfRepository<Project>(dbContext)),
            TimeProvider.System
        );
        var executionClient = new RecordingDurableExecutionClient();
        var executor = new DurableJobAgentExecutor(executionClient, taskExecution);
        var executionId = Guid.CreateVersion7();
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = project.Id,
            AgentType = AgentRuntimeType.Agent,
            AgentId = Guid.CreateVersion7(),
            Name = "Scheduled agent",
            Prompt = "run",
            CreateBy = "owner",
        };

        await executor.ExecuteAsync(job, executionId, cancellationToken);

        Assert.Equal(executionId, executionClient.StartedRequest?.ExecutionId);
        Assert.Equal(executionId, executionClient.WaitedExecutionId);
        Assert.Equal(0, executionClient.ReadCount);
        var task = await taskExecution.GetTaskAsync(executionId);
        Assert.NotNull(task);
        Assert.Equal(TaskExecutionStatus.Running, task.Status);
    }

    private sealed class RecordingDurableExecutionClient : IDurableExecutionClient
    {
        public DurableExecutionRequest? StartedRequest { get; private set; }
        public Guid? WaitedExecutionId { get; private set; }
        public int ReadCount { get; private set; }

        public Task StartAsync(DurableExecutionRequest request, CancellationToken cancellationToken)
        {
            StartedRequest = request;
            return Task.CompletedTask;
        }

        public Task<DurableExecutionOutcome> GetOutcomeAsync(
            Guid executionId,
            string userId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<DurableExecutionOutcome> WaitForActionableOutcomeAsync(
            Guid executionId,
            string userId,
            CancellationToken cancellationToken
        )
        {
            WaitedExecutionId = executionId;
            return Task.FromResult(
                new DurableExecutionOutcome(executionId, DurableExecutionStatus.Completed, ErrorMessage: null)
            );
        }

        public async IAsyncEnumerable<DurableExecutionEvent> ReadAsync(
            Guid executionId,
            string userId,
            string? afterCursor,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            ReadCount++;
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> InterruptAsync(
            Guid executionId,
            string userId,
            string? reason,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }
}
