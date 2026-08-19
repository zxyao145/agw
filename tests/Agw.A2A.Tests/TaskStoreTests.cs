using System.Text.Json;
using A2A;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.A2A.Tests;

public class TaskStoreTests
{
    private static readonly Guid A2AProjectId = Guid.Parse("11111111-1111-1111-1111-000000000003");

    [Fact]
    public async Task SaveTaskAsync_ThenGetTaskAsync_RoundTripsThroughAgwTasksStorage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);
        await SeedA2AProjectAsync(options, cancellationToken);

        await using var dbContext = new AgwDbContext(options);
        var store = CreateStore(dbContext);
        var taskId = Guid.CreateVersion7().ToString("D");

        var task = CreateTask(
            taskId: taskId,
            contextId: "ctx-roundtrip",
            state: TaskState.Working,
            timestamp: new DateTimeOffset(2026, 4, 6, 0, 0, 0, TimeSpan.Zero),
            historyTexts: ["hello", "world"],
            artifactTexts: ["artifact-1"]
        );

        await store.SaveTaskAsync(taskId, task, cancellationToken);

        var loaded = await store.GetTaskAsync(taskId, cancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(taskId, loaded!.Id);
        Assert.Equal("ctx-roundtrip", loaded.ContextId);
        Assert.Equal(TaskState.Working, loaded.Status.State);
        Assert.Equal(task.Status.Timestamp, loaded.Status.Timestamp);
        Assert.Equal(2, loaded.History!.Count);
        Assert.Equal("hello", loaded.History[0].Parts[0].Text);
        var loadedArtifact = Assert.Single(loaded.Artifacts!);
        Assert.Equal("artifact-1", loadedArtifact.Parts![0].Text);
        Assert.Equal("trace-1", loaded.Metadata!["traceId"].GetString());

        var persistedContext = await dbContext.ProjectConversations.SingleAsync(cancellationToken);
        Assert.Equal(A2AProjectId, persistedContext.ProjectId);
        Assert.Equal("ctx-roundtrip", persistedContext.ContextId);
        var persistedRecord = await dbContext.ProjectConversationChatHistories.SingleAsync(
            x => x.TaskId == Guid.Parse(taskId),
            cancellationToken
        );
        Assert.Equal(persistedContext.Id, persistedRecord.ConversationId);
        Assert.Equal(TaskExecutionStatus.Running, persistedRecord.Status);
    }

    [Fact]
    public async Task ListTasksAsync_AppliesFiltersPaginationAndProjectionOptions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);
        await SeedA2AProjectAsync(options, cancellationToken);

        await using var dbContext = new AgwDbContext(options);
        var store = CreateStore(dbContext);

        var firstTaskId = Guid.CreateVersion7().ToString("D");
        var secondTaskId = Guid.CreateVersion7().ToString("D");
        var thirdTaskId = Guid.CreateVersion7().ToString("D");

        await store.SaveTaskAsync(
            firstTaskId,
            CreateTask(
                taskId: firstTaskId,
                contextId: "ctx-list-1",
                state: TaskState.Working,
                timestamp: new DateTimeOffset(2026, 4, 6, 0, 1, 0, TimeSpan.Zero),
                historyTexts: ["one-a", "one-b"],
                artifactTexts: ["artifact-a"]
            ),
            cancellationToken
        );

        await store.SaveTaskAsync(
            secondTaskId,
            CreateTask(
                taskId: secondTaskId,
                contextId: "ctx-list-2",
                state: TaskState.Working,
                timestamp: new DateTimeOffset(2026, 4, 6, 0, 2, 0, TimeSpan.Zero),
                historyTexts: ["two-a", "two-b", "two-c"],
                artifactTexts: ["artifact-b"]
            ),
            cancellationToken
        );

        await store.SaveTaskAsync(
            thirdTaskId,
            CreateTask(
                taskId: thirdTaskId,
                contextId: "ctx-other",
                state: TaskState.Completed,
                timestamp: new DateTimeOffset(2026, 4, 6, 0, 3, 0, TimeSpan.Zero),
                historyTexts: ["three-a"],
                artifactTexts: ["artifact-c"]
            ),
            cancellationToken
        );

        var firstPage = await store.ListTasksAsync(
            new ListTasksRequest
            {
                Status = TaskState.Working,
                PageSize = 1,
                HistoryLength = 1,
                IncludeArtifacts = false,
            },
            cancellationToken
        );

        Assert.Equal(2, firstPage.TotalSize);
        Assert.Equal(1, firstPage.PageSize);
        Assert.Single(firstPage.Tasks);
        Assert.Equal(secondTaskId, firstPage.Tasks[0].Id);
        var firstPageHistoryMessage = Assert.Single(firstPage.Tasks[0].History!);
        Assert.Equal("two-c", firstPageHistoryMessage.Parts![0].Text);
        Assert.Empty(firstPage.Tasks[0].Artifacts!);
        Assert.False(string.IsNullOrWhiteSpace(firstPage.NextPageToken));

        var secondPage = await store.ListTasksAsync(
            new ListTasksRequest
            {
                Status = TaskState.Working,
                PageSize = 1,
                PageToken = firstPage.NextPageToken,
                HistoryLength = 1,
                IncludeArtifacts = false,
            },
            cancellationToken
        );

        Assert.Equal(2, secondPage.TotalSize);
        Assert.Equal(1, secondPage.PageSize);
        Assert.Single(secondPage.Tasks);
        Assert.Equal(firstTaskId, secondPage.Tasks[0].Id);
        Assert.True(string.IsNullOrEmpty(secondPage.NextPageToken));

        var contextFiltered = await store.ListTasksAsync(
            new ListTasksRequest { ContextId = "ctx-list-2" },
            cancellationToken
        );

        Assert.Single(contextFiltered.Tasks);
        Assert.Equal(secondTaskId, contextFiltered.Tasks[0].Id);
    }

    [Fact]
    public void BuiltInProjects_ContainsA2AProjectDefinition()
    {
        var project = Assert.Single(DbSeeder.BuiltInProjects, x => x.Id == A2AProjectId);

        Assert.Equal("a2a", project.Name);
        Assert.Equal(ProjectType.DefaultBuiltIn, project.Type);
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).UseSnakeCaseNamingConvention().Options;

    private static async Task EnsureCreatedAsync(
        DbContextOptions<AgwDbContext> options,
        CancellationToken cancellationToken
    )
    {
        await using var setupContext = new AgwDbContext(options);
        await setupContext.Database.EnsureCreatedAsync(cancellationToken);
    }

    private static async Task SeedA2AProjectAsync(
        DbContextOptions<AgwDbContext> options,
        CancellationToken cancellationToken
    )
    {
        await using var seedContext = new AgwDbContext(options);
        seedContext.Projects.Add(
            new Project
            {
                Id = A2AProjectId,
                Name = "a2a",
                Type = ProjectType.UserDefined,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow(),
            }
        );

        await seedContext.SaveChangesAsync(cancellationToken);
    }

    private static TaskStore CreateStore(AgwDbContext dbContext) =>
        new(
            new EfRepository<ProjectConversation>(dbContext),
            new EfRepository<ProjectConversationChatHistory>(dbContext),
            dbContext,
            TimeProvider.System
        );

    private static AgentTask CreateTask(
        string taskId,
        string contextId,
        TaskState state,
        DateTimeOffset timestamp,
        IReadOnlyList<string> historyTexts,
        IReadOnlyList<string> artifactTexts
    )
    {
        var history = historyTexts
            .Select(
                (text, index) =>
                    new Message
                    {
                        Role = index == 0 ? Role.User : Role.Agent,
                        MessageId = Guid.CreateVersion7().ToString("N"),
                        ContextId = contextId,
                        TaskId = taskId,
                        Parts = [Part.FromText(text)],
                    }
            )
            .ToList();

        var artifacts = artifactTexts
            .Select(text => new Artifact
            {
                ArtifactId = Guid.CreateVersion7().ToString("N"),
                Parts = [Part.FromText(text)],
            })
            .ToList();

        return new AgentTask
        {
            Id = taskId,
            ContextId = contextId,
            Status = new global::A2A.TaskStatus
            {
                State = state,
                Timestamp = timestamp,
                Message = new Message
                {
                    Role = Role.Agent,
                    MessageId = Guid.CreateVersion7().ToString("N"),
                    ContextId = contextId,
                    TaskId = taskId,
                    Parts = [Part.FromText($"status-{state}")],
                },
            },
            History = history,
            Artifacts = artifacts,
            Metadata = new Dictionary<string, JsonElement>
            {
                ["traceId"] = JsonDocument.Parse("\"trace-1\"").RootElement.Clone(),
            },
        };
    }
}
