using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Domain.Services;
using Agw.Infrastructure.Data;
using Agw.Projects.Domain.Services;
using Agw.Shared;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Projects.Tests;

public class EfCoreChatHistoryProviderTests
{
    [Fact]
    public void TryGetProjectContext_InitializedSession_ReturnsProjectAndContext()
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => null!);
        using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System
        );
        var session = new FakeAgentSession();
        var projectId = Guid.CreateVersion7();

        provider.InitializeSessionState(session, " context-1 ", projectId);

        var found = provider.TryGetProjectContext(session, out var resolvedProjectId, out var contextId);

        Assert.True(found);
        Assert.Equal(projectId, resolvedProjectId);
        Assert.Equal("context-1", contextId);
    }

    [Fact]
    public void TryGetProjectContext_UninitializedSession_ReturnsFalse()
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => null!);
        using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System
        );

        var found = provider.TryGetProjectContext(new FakeAgentSession(), out var projectId, out var contextId);

        Assert.False(found);
        Assert.Equal(Guid.Empty, projectId);
        Assert.Equal(string.Empty, contextId);
    }

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

        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        var otherContextId = Guid.CreateVersion7();
        var currentTaskId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectConversations.AddRange(
                CreateContext(projectConversationId, projectId, "context-1"),
                CreateContext(otherContextId, projectId, "context-2")
            );
            seedContext.ProjectConversationChatHistories.AddRange(
                CreateRecord(projectConversationId, Guid.CreateVersion7(), 0, "first", jsonOptions),
                CreateRecord(projectConversationId, currentTaskId, 1, "second", jsonOptions),
                CreateRecord(otherContextId, Guid.CreateVersion7(), 0, "other", jsonOptions)
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions
        );

        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        var messages = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
            cancellationToken
        );

        Assert.Equal(["first", "second"], messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_WhenContextContainsControlSnapshots_ExcludesThemFromModelHistory()
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

        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            seedContext.ProjectConversationChatHistories.AddRange(
                CreateRecord(projectConversationId, Guid.CreateVersion7(), 0, "normal", jsonOptions),
                CreateRecord(
                    projectConversationId,
                    Guid.CreateVersion7(),
                    1,
                    CreateResultMessage("summary"),
                    jsonOptions
                ),
                CreateRecord(projectConversationId, Guid.CreateVersion7(), 2, CreateCheckpointMessage(), jsonOptions)
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions
        );
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        var messages = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
            cancellationToken
        );

        Assert.Equal(["normal"], messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_WhenContextContainsToolBlockState_ExcludesItFromModelHistory()
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

        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            seedContext.ProjectConversationChatHistories.AddRange(
                CreateRecord(projectConversationId, Guid.CreateVersion7(), 0, "normal", jsonOptions),
                CreateRecord(
                    projectConversationId,
                    Guid.CreateVersion7(),
                    1,
                    CreateToolBlockMessage(ToolMessageTypes.TodoSnapshot),
                    jsonOptions
                )
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions
        );
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        var messages = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
            cancellationToken
        );

        Assert.Equal(["normal"], messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_WhenToolApprovalRequestHasNoResponse_DoesNotBreakNextTurn()
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

        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var approvalRequest = new ToolApprovalRequestContent(
            "approval-1",
            new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>())
        );
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            seedContext.ProjectConversationChatHistories.Add(
                CreateRecord(
                    projectConversationId,
                    Guid.CreateVersion7(),
                    0,
                    new ChatMessage(ChatRole.Assistant, [approvalRequest]),
                    jsonOptions
                )
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions
        );
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        var messages = (
            await InvokeProvideChatHistoryAsync(
                provider,
                new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
                cancellationToken
            )
        ).Append(new ChatMessage(ChatRole.User, "continue"));
        var innerClient = new RecordingChatClient();
        using var client = new FunctionInvokingChatClient(innerClient);

        await foreach (var _ in client.GetStreamingResponseAsync(messages, cancellationToken: cancellationToken)) { }

        Assert.Equal(1, innerClient.StreamingCallCount);
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_WhenFunctionResultIsOrphaned_ExcludesItAndPreservesMatchedPair()
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

        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            seedContext.ProjectConversationChatHistories.AddRange(
                CreateRecord(
                    projectConversationId,
                    Guid.CreateVersion7(),
                    0,
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent("matched-call", "read_file", new Dictionary<string, object?>())]
                    ),
                    jsonOptions
                ),
                CreateRecord(
                    projectConversationId,
                    Guid.CreateVersion7(),
                    1,
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent("matched-call", "matched result")]),
                    jsonOptions
                ),
                CreateRecord(
                    projectConversationId,
                    Guid.CreateVersion7(),
                    2,
                    new ChatMessage(ChatRole.Assistant, "completed"),
                    jsonOptions
                ),
                CreateRecord(
                    projectConversationId,
                    Guid.CreateVersion7(),
                    3,
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent("orphaned-call", "orphaned result")]),
                    jsonOptions
                ),
                CreateRecord(
                    projectConversationId,
                    Guid.CreateVersion7(),
                    4,
                    new ChatMessage(ChatRole.User, "continue"),
                    jsonOptions
                )
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions
        );
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        var messages = (
            await InvokeProvideChatHistoryAsync(
                provider,
                new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
                cancellationToken
            )
        ).ToList();

        Assert.Collection(
            messages,
            message =>
                Assert.Equal(
                    "matched-call",
                    Assert.IsType<FunctionCallContent>(Assert.Single(message.Contents)).CallId
                ),
            message =>
                Assert.Equal(
                    "matched-call",
                    Assert.IsType<FunctionResultContent>(Assert.Single(message.Contents)).CallId
                ),
            message => Assert.Equal("completed", message.Text),
            message => Assert.Equal("continue", message.Text)
        );
        Assert.DoesNotContain(
            messages.SelectMany(message => message.Contents),
            content => content is FunctionResultContent { CallId: "orphaned-call" }
        );
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_WhenFunctionCallsArePartiallyCompleted_RemovesCallsWithoutResults()
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

        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            seedContext.ProjectConversationChatHistories.AddRange(
                CreateRecord(
                    projectConversationId,
                    Guid.CreateVersion7(),
                    0,
                    new ChatMessage(
                        ChatRole.Assistant,
                        [
                            new TextContent("working"),
                            new FunctionCallContent("matched-call", "read_file", new Dictionary<string, object?>()),
                            new FunctionCallContent("missing-call", "write_file", new Dictionary<string, object?>()),
                        ]
                    ),
                    jsonOptions
                ),
                CreateRecord(
                    projectConversationId,
                    Guid.CreateVersion7(),
                    1,
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent("matched-call", "matched result")]),
                    jsonOptions
                ),
                CreateRecord(
                    projectConversationId,
                    Guid.CreateVersion7(),
                    2,
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent("interrupted-call", "read_file", new Dictionary<string, object?>())]
                    ),
                    jsonOptions
                ),
                CreateRecord(
                    projectConversationId,
                    Guid.CreateVersion7(),
                    3,
                    new ChatMessage(ChatRole.User, "continue"),
                    jsonOptions
                )
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions
        );
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        var messages = (
            await InvokeProvideChatHistoryAsync(
                provider,
                new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
                cancellationToken
            )
        ).ToList();

        Assert.Collection(
            messages,
            message =>
            {
                Assert.Equal("working", Assert.IsType<TextContent>(message.Contents[0]).Text);
                Assert.Equal("matched-call", Assert.IsType<FunctionCallContent>(message.Contents[1]).CallId);
                Assert.Equal(2, message.Contents.Count);
            },
            message =>
                Assert.Equal(
                    "matched-call",
                    Assert.IsType<FunctionResultContent>(Assert.Single(message.Contents)).CallId
                ),
            message => Assert.Equal("continue", message.Text)
        );
        Assert.DoesNotContain(
            messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>(),
            content => content.CallId is "missing-call" or "interrupted-call"
        );
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_WhenContextMessagePrecedesCurrentFunctionResult_PreservesFunctionCall()
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

        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            seedContext.ProjectConversationChatHistories.Add(
                CreateRecord(
                    projectConversationId,
                    Guid.CreateVersion7(),
                    0,
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent("call-1", "todos_add", new Dictionary<string, object?>())]
                    ),
                    jsonOptions
                )
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions
        );
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);
        var currentToolResult = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "todo added")]);
        var contextMessage = new ChatMessage(ChatRole.User, "current todo context").WithAgentRequestMessageSource(
            AgentRequestMessageSourceType.AIContextProvider,
            "TodoProvider"
        );

        var messages = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, [contextMessage, currentToolResult]),
            cancellationToken
        );

        var message = Assert.Single(messages);
        Assert.Equal("call-1", Assert.IsType<FunctionCallContent>(Assert.Single(message.Contents)).CallId);
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_WhenToolMessageHasPortableContent_PreservesIt()
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

        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            seedContext.ProjectConversationChatHistories.Add(
                CreateRecord(
                    projectConversationId,
                    Guid.CreateVersion7(),
                    0,
                    new ChatMessage(ChatRole.Tool, "portable tool content"),
                    jsonOptions
                )
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions
        );
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        var messages = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
            cancellationToken
        );

        var message = Assert.Single(messages);
        Assert.Equal(ChatRole.Tool, message.Role);
        Assert.Equal("portable tool content", message.Text);
    }

    [Fact]
    public async Task ScopedSessions_ShareProjectConversationButLoadOnlyOwnHistory()
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
            setupContext.Projects.Add(CreateProject(Guid.Parse("11111111-1111-1111-1111-111111111111")));
            await setupContext.SaveChangesAsync(cancellationToken);
        }

        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System
        );
        var firstSession = new FakeAgentSession();
        var secondSession = new FakeAgentSession();
        var unscopedSession = new FakeAgentSession();
        provider.InitializeSessionState(firstSession, "context-1", projectId, "agentflow:flow:node-a");
        provider.InitializeSessionState(secondSession, "context-1", projectId, "agentflow:flow:node-b");
        provider.InitializeSessionState(unscopedSession, "context-1", projectId);

        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(
                new FakeAgent(),
                firstSession,
                [new ChatMessage(ChatRole.User, "first private history")],
                []
            ),
            cancellationToken
        );
        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(
                new FakeAgent(),
                unscopedSession,
                [new ChatMessage(ChatRole.User, "unscoped history")],
                []
            ),
            cancellationToken
        );
        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(
                new FakeAgent(),
                secondSession,
                [new ChatMessage(ChatRole.User, "second private history")],
                []
            ),
            cancellationToken
        );

        var firstHistory = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), firstSession, []),
            cancellationToken
        );
        var secondHistory = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), secondSession, []),
            cancellationToken
        );
        var unscopedHistory = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), unscopedSession, []),
            cancellationToken
        );

        Assert.Equal(["first private history"], firstHistory.Select(message => message.Text));
        Assert.Equal(["second private history"], secondHistory.Select(message => message.Text));
        Assert.Equal(["unscoped history"], unscopedHistory.Select(message => message.Text));

        await using var verifyContext = new AgwDbContext(options);
        Assert.Single(await verifyContext.ProjectConversations.ToListAsync(cancellationToken));
        var records = await verifyContext
            .ProjectConversationChatHistories.OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        var scopes = records
            .Select(record =>
                record.Metadata?.TryGetValue("historyScope", out var historyScope) == true
                    ? historyScope.GetString()
                    : null
            )
            .ToList();
        Assert.Equal(["agentflow:flow:node-a", null, "agentflow:flow:node-b"], scopes);
    }

    [Fact]
    public async Task StoreChatHistoryAsync_NodeScopedSession_PersistsDisplayMetadataOnlyOnResponses()
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

        var projectId = Guid.CreateVersion7();
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
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System
        );
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId, "agentflow:flow:node-a", "  Review Node  ");
        var displayOnlyMessage = new ChatMessage(ChatRole.System, "working") { MessageId = "progress-1" };
        ConversationHistoryMetadata.ExcludeFromModelHistory(displayOnlyMessage);

        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(
                new FakeAgent("  general-agent  "),
                session,
                [new ChatMessage(ChatRole.User, "question") { MessageId = "user-1" }],
                [
                    new ChatMessage(ChatRole.Assistant, "answer") { MessageId = "assistant-1" },
                    new ChatMessage(ChatRole.Assistant, "nested answer")
                    {
                        MessageId = "assistant-2",
                        AuthorName = "inner-agent",
                        AdditionalProperties = new AdditionalPropertiesDictionary
                        {
                            ["nodeName"] = "Inner Node",
                            ["marker"] = "kept",
                        },
                    },
                    displayOnlyMessage,
                ]
            ),
            cancellationToken
        );

        await using var verifyContext = new AgwDbContext(options);
        var records = await verifyContext
            .ProjectConversationChatHistories.OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        var messages = records
            .Select(record =>
                JsonSerializer.Deserialize<ChatMessage>(
                    record.ConversationPayload!,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                )!
            )
            .ToList();

        Assert.Equal(
            ["user-1", "assistant-1", "assistant-2", "progress-1"],
            messages.Select(message => message.MessageId)
        );
        Assert.Null(messages[0].AdditionalProperties);
        Assert.Null(messages[1].AuthorName);
        Assert.Equal("Review Node", messages[1].AdditionalProperties!["nodeName"]?.ToString());
        Assert.Equal("general-agent", messages[1].AdditionalProperties!["agentName"]?.ToString());
        Assert.Equal("inner-agent", messages[2].AuthorName);
        Assert.Equal("Inner Node", messages[2].AdditionalProperties!["nodeName"]?.ToString());
        Assert.False(messages[2].AdditionalProperties!.ContainsKey("agentName"));
        Assert.Equal("kept", messages[2].AdditionalProperties!["marker"]?.ToString());
        Assert.True(ConversationHistoryMetadata.IsModelHistoryExcluded(messages[3]));
        Assert.Equal("Review Node", messages[3].AdditionalProperties!["nodeName"]?.ToString());
        Assert.Equal("general-agent", messages[3].AdditionalProperties!["agentName"]?.ToString());
        Assert.All(
            records,
            record => Assert.Equal("agentflow:flow:node-a", record.Metadata!["historyScope"].GetString())
        );

        var modelHistory = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
            cancellationToken
        );
        Assert.Equal(["question", "answer", "nested answer"], modelHistory.Select(message => message.Text));
    }

    [Fact]
    public async Task AppendAsync_WhenMessagesContainBlankText_PersistsOnlyMessagesWithContent()
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

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        IConversationHistoryWriter writer = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System
        );

        await writer.AppendAsync(
            projectId,
            "context-1",
            [
                new ChatMessage(ChatRole.Assistant, string.Empty),
                new ChatMessage(ChatRole.Assistant, "   "),
                new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("\t")]),
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new TextContent(string.Empty),
                        new TextReasoningContent(" "),
                        new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>()),
                    ]
                ),
                new ChatMessage(ChatRole.Assistant, [new TextContent(string.Empty), new TextContent("hello")]),
                new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("kept reasoning")]),
            ],
            cancellationToken
        );

        await using var verifyContext = new AgwDbContext(options);
        var records = await verifyContext
            .ProjectConversationChatHistories.OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        var persisted = records
            .Select(record =>
                JsonSerializer.Deserialize<ChatMessage>(
                    record.ConversationPayload!,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                )!
            )
            .ToList();
        Assert.Collection(
            persisted,
            message =>
                Assert.Equal("call-1", Assert.IsType<FunctionCallContent>(Assert.Single(message.Contents)).CallId),
            message => Assert.Equal("hello", Assert.IsType<TextContent>(Assert.Single(message.Contents)).Text),
            message =>
                Assert.Equal(
                    "kept reasoning",
                    Assert.IsType<TextReasoningContent>(Assert.Single(message.Contents)).Text
                )
        );
    }

    [Fact]
    public async Task AppendAsync_EmptyToolBlockState_PersistsForConversationHistory()
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

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        IConversationHistoryWriter writer = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System
        );

        await writer.AppendAsync(
            projectId,
            "context-1",
            [CreateToolBlockMessage(ToolMessageTypes.TodoSnapshot)],
            cancellationToken
        );

        await using var verifyContext = new AgwDbContext(options);
        var record = await verifyContext.ProjectConversationChatHistories.SingleAsync(cancellationToken);
        var persisted = JsonSerializer.Deserialize<ChatMessage>(
            record.ConversationPayload!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );
        Assert.NotNull(persisted);
        Assert.Equal(ToolMessageTypes.TodoSnapshot, persisted.AdditionalProperties!["type"]?.ToString());
        Assert.Equal(string.Empty, Assert.IsType<TextContent>(Assert.Single(persisted.Contents)).Text);
    }

    [Fact]
    public async Task AppendAsync_ResultMessage_PersistsItForConversationHistory()
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

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        IConversationHistoryWriter writer = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System
        );

        await writer.AppendAsync(projectId, "context-1", [CreateResultMessage("summary")], cancellationToken);

        await using var verifyContext = new AgwDbContext(options);
        var record = await verifyContext.ProjectConversationChatHistories.SingleAsync(cancellationToken);
        Assert.Contains("summary", record.ConversationPayload);
        var persisted = JsonSerializer.Deserialize<ChatMessage>(
            record.ConversationPayload!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );
        Assert.NotNull(persisted);
        Assert.Equal("summary", Assert.IsType<TextContent>(Assert.Single(persisted.Contents)).Text);
        Assert.Equal("result", persisted.AdditionalProperties!["type"]?.ToString());
    }

    [Fact]
    public async Task AppendAsync_WithLegacyUppercaseGuidContext_ReusesAndNormalizesContext()
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

        var projectId = Guid.CreateVersion7();
        var contextGuid = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectConversations.Add(
                CreateContext(projectConversationId, projectId, contextGuid.ToString("D").ToUpperInvariant())
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        IConversationHistoryWriter writer = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System
        );

        await writer.AppendAsync(
            projectId,
            contextGuid.Normalize(),
            [new ChatMessage(ChatRole.User, "continue")],
            cancellationToken
        );

        await using var verifyContext = new AgwDbContext(options);
        var persistedContext = Assert.Single(await verifyContext.ProjectConversations.ToListAsync(cancellationToken));
        Assert.Equal(projectConversationId, persistedContext.Id);
        Assert.Equal(contextGuid.Normalize(), persistedContext.ContextId);
        Assert.Equal(
            projectConversationId,
            (await verifyContext.ProjectConversationChatHistories.SingleAsync(cancellationToken)).ConversationId
        );
    }

    [Fact]
    public async Task StoreChatHistoryAsync_GeneratesTaskIdPerSaveBatch()
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

        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            seedContext.ProjectConversationChatHistories.Add(
                CreateRecord(projectConversationId, Guid.CreateVersion7(), 0, "existing", jsonOptions)
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions
        );

        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(
                new FakeAgent(),
                session,
                [new ChatMessage(ChatRole.User, "new")],
                [new ChatMessage(ChatRole.Assistant, "response")]
            ),
            cancellationToken
        );

        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(
                new FakeAgent(),
                session,
                [new ChatMessage(ChatRole.User, "next")],
                []
            ),
            cancellationToken
        );

        await using var verifyContext = new AgwDbContext(options);
        var records = await verifyContext
            .ProjectConversationChatHistories.Where(record => record.ConversationId == projectConversationId)
            .OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);

        Assert.Equal([0, 1, 2, 3], records.Select(record => record.ConversationSequence));
        Assert.NotEqual(Guid.Empty, records[1].TaskId);
        Assert.Equal(records[1].TaskId, records[2].TaskId);
        Assert.NotEqual(records[1].TaskId, records[3].TaskId);
    }

    [Fact]
    public async Task StoreChatHistoryAsync_WithPreludeMessage_PersistsItBetweenRequestAndResponse()
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

        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions
        );
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);
        ConversationHistoryPrelude.Set(
            session,
            [
                new ChatMessage(ChatRole.System, "fallback warning")
                {
                    MessageId = "warning-1",
                    AuthorName = "tools",
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = ToolMessageTypes.Warning },
                },
            ]
        );

        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(
                new FakeAgent(),
                session,
                [new ChatMessage(ChatRole.User, "question") { MessageId = "user-1" }],
                [new ChatMessage(ChatRole.Assistant, "answer") { MessageId = "assistant-1" }]
            ),
            cancellationToken
        );

        await using var verifyContext = new AgwDbContext(options);
        var records = await verifyContext
            .ProjectConversationChatHistories.Where(record => record.ConversationId == projectConversationId)
            .OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        var messages = records
            .Select(record => JsonSerializer.Deserialize<ChatMessage>(record.ConversationPayload!, jsonOptions)!)
            .ToList();

        Assert.Equal(["user-1", "warning-1", "assistant-1"], messages.Select(message => message.MessageId));
        Assert.Equal([0, 1, 2], records.Select(record => record.ConversationSequence));
        Assert.Empty(ConversationHistoryPrelude.Take(session));
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

        var projectId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = projectId,
                    ContextId = "context-1",
                    Title = "New Chat",
                    CreateBy = "tester",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System
        );

        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);
        var message = new ChatMessage(ChatRole.User, "Draft the launch plan")
        {
            MessageId = Guid.CreateVersion7().ToString(),
            AuthorName = "tester",
        };

        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(new FakeAgent(), session, [message], []),
            cancellationToken
        );

        await using var verifyContext = new AgwDbContext(options);
        var projectConversation = await verifyContext.ProjectConversations.SingleAsync(cancellationToken);
        Assert.Equal("Draft the launch plan", projectConversation.Title);
    }

    [Fact]
    public async Task StoreChatHistoryAsync_DataContent_PersistsAndRestoresImage()
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

        var projectId = Guid.CreateVersion7();
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
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System
        );
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);
        var request = new ChatMessage(
            ChatRole.User,
            [
                new DataContent(new byte[] { 1, 2, 3 }, "image/png") { Name = "screen.png" },
                new TextContent("describe this"),
            ]
        )
        {
            MessageId = "user-with-image",
        };

        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(new FakeAgent(), session, [request], []),
            cancellationToken
        );

        var history = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
            cancellationToken
        );
        var restored = Assert.Single(history);
        var image = Assert.IsType<DataContent>(restored.Contents[0]);

        Assert.Equal("user-with-image", restored.MessageId);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Data.ToArray());
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("screen.png", image.Name);
        Assert.Equal("describe this", Assert.IsType<TextContent>(restored.Contents[1]).Text);
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

        var projectId = Guid.CreateVersion7();
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
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System
        );

        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        var message = new ChatMessage(
            ChatRole.User,
            [
                new TextContent("hello")
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["targetType"] = "agent",
                        ["targetId"] = "11111111-1111-1111-1111-111111111111",
                    },
                },
            ]
        )
        {
            MessageId = Guid.CreateVersion7().ToString(),
            AuthorName = "tester",
        };

        var context = new ChatHistoryProvider.InvokedContext(new FakeAgent(), session, [message], []);

        await InvokeStoreChatHistoryAsync(provider, context, cancellationToken);

        await using var verifyContext = new AgwDbContext(options);
        var record = await verifyContext.ProjectConversationChatHistories.SingleAsync(cancellationToken);

        Assert.NotNull(record.Metadata);
        Assert.Equal("agent", record.Metadata!["targetType"].GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", record.Metadata["targetId"].GetString());
    }

    [Fact]
    public async Task StoreChatHistoryAsync_HandoffAndCurrentInput_PersistsOnlyCurrentInputAndCursor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        var projectId = Guid.CreateVersion7();
        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            setupContext.Projects.Add(CreateProject(projectId));
            await setupContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System
        );
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);
        var handoff = new ChatMessage(ChatRole.Assistant, "previous plan");
        ConversationHandoffMetadata.MarkHandoffMessage(handoff);
        var current = new ChatMessage(ChatRole.User, "implement it")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ConversationHandoffMetadata.ThroughSequenceKey] = 17L,
            },
        };

        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(new FakeAgent(), session, [handoff, current], []),
            cancellationToken
        );

        await using var verifyContext = new AgwDbContext(options);
        var record = await verifyContext.ProjectConversationChatHistories.SingleAsync(cancellationToken);
        Assert.Equal("implement it", record.GetText());
        Assert.Equal(17, record.Metadata![ConversationHandoffMetadata.ThroughSequenceKey].GetInt64());
    }

    [Fact]
    public async Task AppendAsync_ConcurrentCalls_AssignsUniqueOrderedSequences()
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
            setupContext.Projects.Add(CreateProject(Guid.Parse("11111111-1111-1111-1111-111111111111")));
            await setupContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        IConversationHistoryWriter writer = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new InMemoryApplicationLock(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System
        );
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = Enumerable
            .Range(0, 12)
            .Select(async index =>
            {
                await start.Task;
                await writer.AppendAsync(
                    projectId,
                    "context-1",
                    [new ChatMessage(ChatRole.User, $"message-{index}")],
                    cancellationToken
                );
            })
            .ToArray();

        start.SetResult();
        await Task.WhenAll(writes);

        await using var verificationContext = new AgwDbContext(options);
        var context = await verificationContext.ProjectConversations.SingleAsync(cancellationToken);
        Assert.Equal(Constants.AdminUserId, context.CreateBy);
        Assert.Equal(Constants.AdminUserId, context.UpdateBy);
        var sequences = await verificationContext
            .ProjectConversationChatHistories.Where(record => record.ConversationId == context.Id)
            .OrderBy(record => record.ConversationSequence)
            .Select(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        Assert.Equal(Enumerable.Range(0, 12).Select(static value => (long?)value), sequences);
    }

    private static Project CreateProject(Guid projectId) =>
        new()
        {
            Id = projectId,
            Name = "Chat Project",
            Type = ProjectType.UserDefined,
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static ProjectConversation CreateContext(Guid id, Guid projectId, string contextId) =>
        new()
        {
            Id = id,
            ProjectId = projectId,
            ContextId = contextId,
            Title = "Chat",
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
            UpdateBy = "tester",
            UpdateTime = TimeProvider.System.GetUtcNow(),
        };

    private static ProjectConversationChatHistory CreateRecord(
        Guid projectConversationId,
        Guid taskId,
        long sequence,
        string text,
        JsonSerializerOptions jsonOptions
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ConversationId = projectConversationId,
            TaskId = taskId,
            Status = TaskExecutionStatus.Succeeded,
            ConversationSequence = sequence,
            ConversationPayload = JsonSerializer.Serialize(new ChatMessage(ChatRole.User, text), jsonOptions),
            CreateTime = TimeProvider.System.GetUtcNow(),
            UpdateTime = TimeProvider.System.GetUtcNow(),
        };

    private static ProjectConversationChatHistory CreateRecord(
        Guid projectConversationId,
        Guid taskId,
        long sequence,
        ChatMessage message,
        JsonSerializerOptions jsonOptions
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ConversationId = projectConversationId,
            TaskId = taskId,
            Status = TaskExecutionStatus.Succeeded,
            ConversationSequence = sequence,
            ConversationPayload = JsonSerializer.Serialize(message, jsonOptions),
            CreateTime = TimeProvider.System.GetUtcNow(),
            UpdateTime = TimeProvider.System.GetUtcNow(),
        };

    private static ChatMessage CreateResultMessage(string text) =>
        new(ChatRole.System, text)
        {
            MessageId = Guid.CreateVersion7().ToString(),
            AuthorName = Constants.DefaultAgentAuthor,
            AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = "result" },
        };

    private static ChatMessage CreateCheckpointMessage() =>
        new(ChatRole.Assistant, "Review saved")
        {
            MessageId = Guid.CreateVersion7().ToString(),
            AuthorName = Constants.DefaultAgentAuthor,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["type"] = "agentflow-checkpoint",
                ["checkpointOccurrenceId"] = Guid.CreateVersion7().ToString(),
            },
        };

    private static ChatMessage CreateToolBlockMessage(string type) =>
        new(ChatRole.System, [new TextContent(string.Empty)])
        {
            MessageId = Guid.CreateVersion7().ToString(),
            AuthorName = "tools",
            AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = type },
        };

    private static async Task<IEnumerable<ChatMessage>> InvokeProvideChatHistoryAsync(
        EfCoreChatHistoryProvider provider,
        ChatHistoryProvider.InvokingContext context,
        CancellationToken cancellationToken
    )
    {
        var method = typeof(EfCoreChatHistoryProvider).GetMethod(
            "ProvideChatHistoryAsync",
            BindingFlags.NonPublic | BindingFlags.Instance
        );

        Assert.NotNull(method);

        var valueTask = (ValueTask<IEnumerable<ChatMessage>>)method!.Invoke(provider, [context, cancellationToken])!;
        return await valueTask;
    }

    private static async Task InvokeStoreChatHistoryAsync(
        EfCoreChatHistoryProvider provider,
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken
    )
    {
        var method = typeof(EfCoreChatHistoryProvider).GetMethod(
            "StoreChatHistoryAsync",
            BindingFlags.NonPublic | BindingFlags.Instance
        );

        Assert.NotNull(method);

        var valueTask = (ValueTask)method!.Invoke(provider, [context, cancellationToken])!;
        await valueTask;
    }

    private sealed class FakeAgent : AIAgent
    {
        private readonly string? _name;

        public FakeAgent(string? name = null)
        {
            _name = name;
        }

        public override string? Name => _name;

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new FakeAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new FakeAgentSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            yield break;
        }
    }

    private sealed class FakeAgentSession : AgentSession;

    private sealed class RecordingChatClient : IChatClient
    {
        public int StreamingCallCount { get; private set; }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            StreamingCallCount++;
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
            await Task.CompletedTask;
        }
    }
}
