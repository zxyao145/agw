using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Domain.Services;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;

using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Tasks.Tests;

public class EfCoreChatHistoryProviderTests
{
    [Fact]
    public async Task ProvideChatHistoryAsync_WhenContextHasMultipleTasks_ReturnsAllContextMessages()
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
        var otherContextId = Guid.NewGuid();
        var currentTaskId = Guid.NewGuid();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectContexts.AddRange(
                CreateContext(projectContextId, projectId, "context-1"),
                CreateContext(otherContextId, projectId, "context-2"));
            seedContext.TaskRecords.AddRange(
                CreateRecord(projectContextId, Guid.NewGuid(), 0, "first", jsonOptions),
                CreateRecord(projectContextId, currentTaskId, 1, "second", jsonOptions),
                CreateRecord(otherContextId, Guid.NewGuid(), 0, "other", jsonOptions));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            jsonOptions);

        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", currentTaskId.ToString("D"), projectId);

        var messages = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
            cancellationToken);

        Assert.Equal(["first", "second"], messages.Select(message => message.Text));
    }

    [Fact]
    public async Task StoreChatHistoryAsync_UsesContextScopedConversationSequence()
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
        var currentTaskId = Guid.NewGuid();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectContexts.Add(CreateContext(projectContextId, projectId, "context-1"));
            seedContext.TaskRecords.Add(CreateRecord(projectContextId, Guid.NewGuid(), 0, "existing", jsonOptions));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            jsonOptions);

        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", currentTaskId.ToString("D"), projectId);

        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(
                new FakeAgent(),
                session,
                [new ChatMessage(ChatRole.User, "new")],
                []),
            cancellationToken);

        await using var verifyContext = new AgwDbContext(options);
        var records = await verifyContext.TaskRecords
            .Where(record => record.ProjectContextId == projectContextId)
            .OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);

        Assert.Equal([0, 1], records.Select(record => record.ConversationSequence));
        Assert.Equal(currentTaskId, records[1].TaskId);
    }

    [Fact]
    public async Task StoreChatHistoryAsync_WhenExistingContextUsesFallbackTitle_UpdatesTitleFromFirstUserMessage()
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
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectContexts.Add(new ProjectContext
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                ContextId = "context-1",
                Title = "New Chat",
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance);

        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", Guid.NewGuid().ToString("D"), projectId);
        var message = new ChatMessage(ChatRole.User, "Draft the launch plan")
        {
            MessageId = Guid.NewGuid().ToString(),
            AuthorName = "tester"
        };

        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(new FakeAgent(), session, [message], []),
            cancellationToken);

        await using var verifyContext = new AgwDbContext(options);
        var projectContext = await verifyContext.ProjectContexts.SingleAsync(cancellationToken);
        Assert.Equal("Draft the launch plan", projectContext.Title);
    }

    [Fact]
    public async Task StoreChatHistoryAsync_PersistsTargetMetadataFromRequestMessage()
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
            seedContext.Projects.Add(CreateProject(projectId));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance);

        var session = new FakeAgentSession();
        var taskId = Guid.NewGuid();
        provider.InitializeSessionState(session, "context-1", taskId.ToString("D"), projectId);

        var message = new ChatMessage(
            ChatRole.User,
            [
                new TextContent("hello")
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["targetType"] = "agent",
                        ["targetId"] = "11111111-1111-1111-1111-111111111111"
                    }
                }
            ])
        {
            MessageId = Guid.NewGuid().ToString(),
            AuthorName = "tester"
        };

        var context = new ChatHistoryProvider.InvokedContext(
            new FakeAgent(),
            session,
            [message],
            []);

        await InvokeStoreChatHistoryAsync(provider, context, cancellationToken);

        await using var verifyContext = new AgwDbContext(options);
        var record = await verifyContext.TaskRecords.SingleAsync(
            x => x.TaskId == taskId,
            cancellationToken);

        Assert.NotNull(record.Metadata);
        Assert.Equal("agent", record.Metadata!["targetType"].GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", record.Metadata["targetId"].GetString());
    }

    private static Project CreateProject(Guid projectId) => new()
    {
        Id = projectId,
        Name = "Chat Project",
        Type = ProjectType.UserDefined,
        Enable = true,
        CreateBy = "tester",
        CreateTime = DateTime.UtcNow
    };

    private static ProjectContext CreateContext(Guid id, Guid projectId, string contextId) => new()
    {
        Id = id,
        ProjectId = projectId,
        ContextId = contextId,
        Title = "Chat",
        CreateBy = "tester",
        CreateTime = DateTime.UtcNow,
        UpdateBy = "tester",
        UpdateTime = DateTime.UtcNow
    };

    private static TaskRecord CreateRecord(
        Guid projectContextId,
        Guid taskId,
        long sequence,
        string text,
        JsonSerializerOptions jsonOptions) => new()
    {
        Id = Guid.NewGuid(),
        ProjectContextId = projectContextId,
        TaskId = taskId,
        Status = TaskExecutionStatus.Succeeded,
        ConversationSequence = sequence,
        ConversationPayload = JsonSerializer.Serialize(new ChatMessage(ChatRole.User, text), jsonOptions),
        CreateTime = DateTime.UtcNow,
        UpdateTime = DateTime.UtcNow
    };

    private static async Task<IEnumerable<ChatMessage>> InvokeProvideChatHistoryAsync(
        EfCoreChatHistoryProvider provider,
        ChatHistoryProvider.InvokingContext context,
        CancellationToken cancellationToken)
    {
        var method = typeof(EfCoreChatHistoryProvider).GetMethod(
            "ProvideChatHistoryAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var valueTask = (ValueTask<IEnumerable<ChatMessage>>)method!.Invoke(provider, [context, cancellationToken])!;
        return await valueTask;
    }

    private static async Task InvokeStoreChatHistoryAsync(
        EfCoreChatHistoryProvider provider,
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken)
    {
        var method = typeof(EfCoreChatHistoryProvider).GetMethod(
            "StoreChatHistoryAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var valueTask = (ValueTask)method!.Invoke(provider, [context, cancellationToken])!;
        await valueTask;
    }

    private sealed class FakeAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new FakeAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new FakeAgentSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield break;
        }
    }

    private sealed class FakeAgentSession : AgentSession;
}
