using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Domain.Services;
using Agw.Infrastructure.Data;
using Agw.Shared;
using Agw.Shared.Contracts.Projects;
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
            TimeProvider.System);
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
            TimeProvider.System);

        var found = provider.TryGetProjectContext(
            new FakeAgentSession(),
            out var projectId,
            out var contextId);

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
        var projectContextId = Guid.CreateVersion7();
        var otherContextId = Guid.CreateVersion7();
        var currentTaskId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectContexts.AddRange(
                CreateContext(projectContextId, projectId, "context-1"),
                CreateContext(otherContextId, projectId, "context-2"));
            seedContext.TaskRecords.AddRange(
                CreateRecord(projectContextId, Guid.CreateVersion7(), 0, "first", jsonOptions),
                CreateRecord(projectContextId, currentTaskId, 1, "second", jsonOptions),
                CreateRecord(otherContextId, Guid.CreateVersion7(), 0, "other", jsonOptions));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions);

        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        var messages = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
            cancellationToken);

        Assert.Equal(["first", "second"], messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_WhenContextContainsResult_ExcludesResultFromModelHistory()
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
        var projectContextId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectContexts.Add(CreateContext(projectContextId, projectId, "context-1"));
            seedContext.TaskRecords.AddRange(
                CreateRecord(projectContextId, Guid.CreateVersion7(), 0, "normal", jsonOptions),
                CreateRecord(projectContextId, Guid.CreateVersion7(), 1, CreateResultMessage("summary"), jsonOptions));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions);
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        var messages = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
            cancellationToken);

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
        var projectContextId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var approvalRequest = new ToolApprovalRequestContent(
            "approval-1",
            new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>()));
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectContexts.Add(CreateContext(projectContextId, projectId, "context-1"));
            seedContext.TaskRecords.Add(CreateRecord(
                projectContextId,
                Guid.CreateVersion7(),
                0,
                new ChatMessage(ChatRole.Assistant, [approvalRequest]),
                jsonOptions));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions);
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        var messages = (await InvokeProvideChatHistoryAsync(
                provider,
                new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
                cancellationToken))
            .Append(new ChatMessage(ChatRole.User, "continue"));
        var innerClient = new RecordingChatClient();
        using var client = new FunctionInvokingChatClient(innerClient);

        await foreach (var _ in client.GetStreamingResponseAsync(messages, cancellationToken: cancellationToken))
        {
        }

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
        var projectContextId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectContexts.Add(CreateContext(projectContextId, projectId, "context-1"));
            seedContext.TaskRecords.AddRange(
                CreateRecord(
                    projectContextId,
                    Guid.CreateVersion7(),
                    0,
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent(
                            "matched-call",
                            "read_file",
                            new Dictionary<string, object?>())]),
                    jsonOptions),
                CreateRecord(
                    projectContextId,
                    Guid.CreateVersion7(),
                    1,
                    new ChatMessage(
                        ChatRole.Tool,
                        [new FunctionResultContent("matched-call", "matched result")]),
                    jsonOptions),
                CreateRecord(
                    projectContextId,
                    Guid.CreateVersion7(),
                    2,
                    new ChatMessage(ChatRole.Assistant, "completed"),
                    jsonOptions),
                CreateRecord(
                    projectContextId,
                    Guid.CreateVersion7(),
                    3,
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent("orphaned-call", "orphaned result")]),
                    jsonOptions),
                CreateRecord(
                    projectContextId,
                    Guid.CreateVersion7(),
                    4,
                    new ChatMessage(ChatRole.User, "continue"),
                    jsonOptions));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions);
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        var messages = (await InvokeProvideChatHistoryAsync(
                provider,
                new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
                cancellationToken))
            .ToList();

        Assert.Collection(
            messages,
            message => Assert.Equal("matched-call", Assert.IsType<FunctionCallContent>(Assert.Single(message.Contents)).CallId),
            message => Assert.Equal("matched-call", Assert.IsType<FunctionResultContent>(Assert.Single(message.Contents)).CallId),
            message => Assert.Equal("completed", message.Text),
            message => Assert.Equal("continue", message.Text));
        Assert.DoesNotContain(messages.SelectMany(message => message.Contents), content =>
            content is FunctionResultContent { CallId: "orphaned-call" });
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
        var projectContextId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectContexts.Add(CreateContext(projectContextId, projectId, "context-1"));
            seedContext.TaskRecords.Add(CreateRecord(
                projectContextId,
                Guid.CreateVersion7(),
                0,
                new ChatMessage(ChatRole.Tool, "portable tool content"),
                jsonOptions));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions);
        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        var messages = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
            cancellationToken);

        var message = Assert.Single(messages);
        Assert.Equal(ChatRole.Tool, message.Role);
        Assert.Equal("portable tool content", message.Text);
    }

    [Fact]
    public async Task ScopedSessions_ShareProjectContextButLoadOnlyOwnHistory()
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
            TimeProvider.System);
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
                []),
            cancellationToken);
        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(
                new FakeAgent(),
                unscopedSession,
                [new ChatMessage(ChatRole.User, "unscoped history")],
                []),
            cancellationToken);
        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(
                new FakeAgent(),
                secondSession,
                [new ChatMessage(ChatRole.User, "second private history")],
                []),
            cancellationToken);

        var firstHistory = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), firstSession, []),
            cancellationToken);
        var secondHistory = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), secondSession, []),
            cancellationToken);
        var unscopedHistory = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), unscopedSession, []),
            cancellationToken);

        Assert.Equal(["first private history"], firstHistory.Select(message => message.Text));
        Assert.Equal(["second private history"], secondHistory.Select(message => message.Text));
        Assert.Equal(["unscoped history"], unscopedHistory.Select(message => message.Text));

        await using var verifyContext = new AgwDbContext(options);
        Assert.Single(await verifyContext.ProjectContexts.ToListAsync(cancellationToken));
        var records = await verifyContext.TaskRecords
            .OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        var scopes = records
            .Select(record =>
                record.Metadata?.TryGetValue("historyScope", out var historyScope) == true
                    ? historyScope.GetString()
                    : null)
            .ToList();
        Assert.Equal(["agentflow:flow:node-a", null, "agentflow:flow:node-b"], scopes);
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
            TimeProvider.System);

        await writer.AppendAsync(
            projectId,
            "context-1",
            [
                new ChatMessage(ChatRole.Assistant, string.Empty),
                new ChatMessage(ChatRole.Assistant, "   "),
                new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>())])
            ],
            cancellationToken);

        await using var verifyContext = new AgwDbContext(options);
        var record = await verifyContext.TaskRecords.SingleAsync(cancellationToken);
        var persisted = JsonSerializer.Deserialize<ChatMessage>(
            record.ConversationPayload!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(persisted);
        var functionCall = Assert.IsType<FunctionCallContent>(Assert.Single(persisted.Contents));
        Assert.Equal("call-1", functionCall.CallId);
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
            TimeProvider.System);

        await writer.AppendAsync(
            projectId,
            "context-1",
            [CreateResultMessage("summary")],
            cancellationToken);

        await using var verifyContext = new AgwDbContext(options);
        var record = await verifyContext.TaskRecords.SingleAsync(cancellationToken);
        Assert.Contains("summary", record.ConversationPayload);
        var persisted = JsonSerializer.Deserialize<ChatMessage>(
            record.ConversationPayload!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
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
        var projectContextId = Guid.CreateVersion7();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectContexts.Add(CreateContext(
                projectContextId,
                projectId,
                contextGuid.ToString("D").ToUpperInvariant()));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        IConversationHistoryWriter writer = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System);

        await writer.AppendAsync(
            projectId,
            contextGuid.Normalize(),
            [new ChatMessage(ChatRole.User, "continue")],
            cancellationToken);

        await using var verifyContext = new AgwDbContext(options);
        var persistedContext = Assert.Single(await verifyContext.ProjectContexts.ToListAsync(cancellationToken));
        Assert.Equal(projectContextId, persistedContext.Id);
        Assert.Equal(contextGuid.Normalize(), persistedContext.ContextId);
        Assert.Equal(
            projectContextId,
            (await verifyContext.TaskRecords.SingleAsync(cancellationToken)).ProjectContextId);
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
        var projectContextId = Guid.CreateVersion7();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(CreateProject(projectId));
            seedContext.ProjectContexts.Add(CreateContext(projectContextId, projectId, "context-1"));
            seedContext.TaskRecords.Add(CreateRecord(projectContextId, Guid.CreateVersion7(), 0, "existing", jsonOptions));
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions);

        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);

        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(
                new FakeAgent(),
                session,
                [new ChatMessage(ChatRole.User, "new")],
                [new ChatMessage(ChatRole.Assistant, "response")]),
            cancellationToken);

        await InvokeStoreChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokedContext(
                new FakeAgent(),
                session,
                [new ChatMessage(ChatRole.User, "next")],
                []),
            cancellationToken);

        await using var verifyContext = new AgwDbContext(options);
        var records = await verifyContext.TaskRecords
            .Where(record => record.ProjectContextId == projectContextId)
            .OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);

        Assert.Equal([0, 1, 2, 3], records.Select(record => record.ConversationSequence));
        Assert.NotEqual(Guid.Empty, records[1].TaskId);
        Assert.Equal(records[1].TaskId, records[2].TaskId);
        Assert.NotEqual(records[1].TaskId, records[3].TaskId);
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
            seedContext.ProjectContexts.Add(new ProjectContext
            {
                Id = Guid.CreateVersion7(),
                ProjectId = projectId,
                ContextId = "context-1",
                Title = "New Chat",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System);

        var session = new FakeAgentSession();
        provider.InitializeSessionState(session, "context-1", projectId);
        var message = new ChatMessage(ChatRole.User, "Draft the launch plan")
        {
            MessageId = Guid.CreateVersion7().ToString(),
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
            TimeProvider.System);

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
                        ["targetId"] = "11111111-1111-1111-1111-111111111111"
                    }
                }
            ])
        {
            MessageId = Guid.CreateVersion7().ToString(),
            AuthorName = "tester"
        };

        var context = new ChatHistoryProvider.InvokedContext(
            new FakeAgent(),
            session,
            [message],
            []);

        await InvokeStoreChatHistoryAsync(provider, context, cancellationToken);

        await using var verifyContext = new AgwDbContext(options);
        var record = await verifyContext.TaskRecords.SingleAsync(cancellationToken);

        Assert.NotNull(record.Metadata);
        Assert.Equal("agent", record.Metadata!["targetType"].GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", record.Metadata["targetId"].GetString());
    }

    private static Project CreateProject(Guid projectId) => new()
    {
        Id = projectId,
        Name = "Chat Project",
        Type = ProjectType.UserDefined,
        CreateBy = "tester",
        CreateTime = TimeProvider.System.GetUtcNow()
    };

    private static ProjectContext CreateContext(Guid id, Guid projectId, string contextId) => new()
    {
        Id = id,
        ProjectId = projectId,
        ContextId = contextId,
        Title = "Chat",
        CreateBy = "tester",
        CreateTime = TimeProvider.System.GetUtcNow(),
        UpdateBy = "tester",
        UpdateTime = TimeProvider.System.GetUtcNow()
    };

    private static TaskRecord CreateRecord(
        Guid projectContextId,
        Guid taskId,
        long sequence,
        string text,
        JsonSerializerOptions jsonOptions) => new()
        {
            Id = Guid.CreateVersion7(),
            ProjectContextId = projectContextId,
            TaskId = taskId,
            Status = TaskExecutionStatus.Succeeded,
            ConversationSequence = sequence,
            ConversationPayload = JsonSerializer.Serialize(new ChatMessage(ChatRole.User, text), jsonOptions),
            CreateTime = TimeProvider.System.GetUtcNow(),
            UpdateTime = TimeProvider.System.GetUtcNow()
        };

    private static TaskRecord CreateRecord(
        Guid projectContextId,
        Guid taskId,
        long sequence,
        ChatMessage message,
        JsonSerializerOptions jsonOptions) => new()
        {
            Id = Guid.CreateVersion7(),
            ProjectContextId = projectContextId,
            TaskId = taskId,
            Status = TaskExecutionStatus.Succeeded,
            ConversationSequence = sequence,
            ConversationPayload = JsonSerializer.Serialize(message, jsonOptions),
            CreateTime = TimeProvider.System.GetUtcNow(),
            UpdateTime = TimeProvider.System.GetUtcNow()
        };

    private static ChatMessage CreateResultMessage(string text) =>
        new(ChatRole.System, text)
        {
            MessageId = Guid.CreateVersion7().ToString(),
            AuthorName = Constants.DefaultAgentAuthor,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["type"] = "result"
            }
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

    private sealed class RecordingChatClient : IChatClient
    {
        public int StreamingCallCount { get; private set; }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingCallCount++;
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("ok")]
            };
            await Task.CompletedTask;
        }
    }
}
