using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.Runtimes;
using Agw.Domain.Services;
using Agw.Infrastructure.Data;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Projects;

using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed class AgwAgentExtensionsTests
{
    [Fact]
    public async Task AsAgwAgent_OwnedCapabilities_DisposeConfiguredChatClientPipeline()
    {
        var client = new StubChatClient();
        var capabilities = CreateCapabilities();
        var innerAgent = client.AsAgwAgent(
            CreateDefinition(),
            capabilities,
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider());
        var agent = new ResourceOwningAIAgent(innerAgent, capabilities);

        await agent.DisposeAsync();

        Assert.True(client.Disposed);
    }

    [Fact]
    public void AsAgwAgent_WithLoopEvaluator_KeepsLoopAgentOutermost()
    {
        var capabilities = CreateCapabilities(
            contextProviders: [new TodoProvider()],
            loopEvaluators: [new StopLoopEvaluator()],
            toolWarnings: ["Hosted web search fell back to local search."]);

        var agent = new StubChatClient()
            .AsAgwAgent(
                CreateDefinition(),
                capabilities,
                NullLoggerFactory.Instance,
                new ServiceCollection().BuildServiceProvider());

        Assert.IsType<LoopAgent>(agent);
        Assert.NotNull(agent.GetService<ToolApprovalAgent>());
        Assert.NotNull(agent.GetService<OpenTelemetryAgent>());
    }

    [Fact]
    public void AsAgwAgent_WithoutLoopEvaluator_StillEnablesToolApprovalAndTelemetry()
    {
        var capabilities = CreateCapabilities();

        var agent = new StubChatClient()
            .AsAgwAgent(
                CreateDefinition(),
                capabilities,
                NullLoggerFactory.Instance,
                new ServiceCollection().BuildServiceProvider());

        Assert.IsType<ToolApprovalAgent>(agent);
        Assert.NotNull(agent.GetService<OpenTelemetryAgent>());
        Assert.Null(agent.GetService<LoopAgent>());
    }

    [Fact]
    public async Task AsAgwAgent_InvocationWarning_EmitsOnlyAfterToolResult()
    {
        var tool = AIFunctionFactory.Create(
            (Func<string>)(() => "search result"),
            new AIFunctionFactoryOptions { Name = "web_search" });
        var agent = new FunctionCallingStubChatClient()
            .AsAgwAgent(
                CreateDefinition(),
                CreateCapabilities(
                    tools: [tool],
                    toolInvocationWarnings: new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["web_search"] = "fallback warning"
                    }),
                NullLoggerFactory.Instance,
                new ServiceCollection().BuildServiceProvider());
        var updates = new List<AgentResponseUpdate>();

        await foreach (var update in agent.RunStreamingAsync(
                           [new ChatMessage(ChatRole.User, "search")],
                           cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        var warning = Assert.Single(
            updates,
            update => update.AdditionalProperties?.TryGetValue("type", out var type) == true &&
                string.Equals(type?.ToString(), "tool-warning", StringComparison.Ordinal));
        Assert.Equal("fallback warning", warning.Text);
        Assert.Contains(
            updates.SelectMany(update => update.Contents),
            content => content is FunctionResultContent);
    }

    [Fact]
    public async Task AsAgwAgent_InvocationWarningWithoutToolCall_DoesNotEmitWarning()
    {
        var agent = new StubChatClient()
            .AsAgwAgent(
                CreateDefinition(),
                CreateCapabilities(
                    toolInvocationWarnings: new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["web_search"] = "fallback warning"
                    }),
                NullLoggerFactory.Instance,
                new ServiceCollection().BuildServiceProvider());
        var updates = new List<AgentResponseUpdate>();

        await foreach (var update in agent.RunStreamingAsync(
                           [new ChatMessage(ChatRole.User, "answer without searching")],
                           cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.DoesNotContain(
            updates,
            update => update.AdditionalProperties?.TryGetValue("type", out var type) == true &&
                string.Equals(type?.ToString(), "tool-warning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AsAgwAgent_TodoMutations_StreamingEmitsSnapshotAfterEachResult()
    {
        var agent = CreateTodoMutationAgent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var updates = new List<AgentResponseUpdate>();

        await foreach (var update in agent.RunStreamingAsync(
                           [new ChatMessage(ChatRole.User, "manage todos")],
                           session,
                           cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        AssertTodoSnapshots(updates);
    }

    [Fact]
    public async Task AsAgwAgent_TodoMutations_NonStreamingEmitsSnapshotAfterEachResult()
    {
        var agent = CreateTodoMutationAgent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "manage todos")],
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        AssertTodoSnapshots(response.Messages.Select(ToolStateSnapshots.ToUpdate).ToList());
    }

    [Fact]
    public async Task AsAgwAgent_ApprovalContinuation_KeepsFunctionResultNextToFunctionCall()
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
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Project",
                Type = ProjectType.UserDefined,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            seedContext.ProjectConversations.Add(new ProjectConversation
            {
                Id = projectConversationId,
                ProjectId = projectId,
                ContextId = "context-1",
                Title = "Chat",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow(),
                UpdateBy = "tester",
                UpdateTime = TimeProvider.System.GetUtcNow()
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var historyProvider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var function = AIFunctionFactory.Create(
            (Func<string>)(() => "written"),
            new AIFunctionFactoryOptions { Name = "web_search" });
        var client = new FunctionCallingStubChatClient();
        var agent = client.AsAgwAgent(
            CreateDefinition(historyProvider),
            CreateCapabilities(
                tools: [new ApprovalRequiredAIFunction(function)],
                contextProviders: [new TodoProvider()]),
            NullLoggerFactory.Instance,
            serviceProvider);
        var session = await agent.CreateSessionAsync(cancellationToken);
        historyProvider.InitializeSessionState(session, "context-1", projectId);
        var firstUpdates = new List<AgentResponseUpdate>();

        await foreach (var update in agent.RunStreamingAsync(
                           [new ChatMessage(ChatRole.User, "write a file")],
                           session,
                           cancellationToken: cancellationToken))
        {
            firstUpdates.Add(update);
        }

        var approvalRequest = Assert.Single(
            firstUpdates.SelectMany(update => update.Contents).OfType<ToolApprovalRequestContent>());
        await foreach (var _ in agent.RunStreamingAsync(
                           [new ChatMessage(ChatRole.User, [approvalRequest.CreateResponse(approved: true)])],
                           session,
                           cancellationToken: cancellationToken))
        {
        }

        var secondRequest = Assert.Single(client.Requests.Skip(1));
        var functionResultIndex = secondRequest.FindIndex(message =>
            message.Contents.OfType<FunctionResultContent>().Any());
        Assert.True(functionResultIndex > 0);
        var functionResult = Assert.Single(
            secondRequest[functionResultIndex].Contents.OfType<FunctionResultContent>());
        var functionCall = Assert.Single(
            secondRequest[functionResultIndex - 1].Contents.OfType<FunctionCallContent>());
        Assert.Equal(ChatRole.Tool, secondRequest[functionResultIndex].Role);
        Assert.Equal(ChatRole.Assistant, secondRequest[functionResultIndex - 1].Role);
        Assert.Equal(functionCall.CallId, functionResult.CallId);

        await using var verifyContext = new AgwDbContext(options);
        var records = await verifyContext.ProjectConversationChatHistories
            .Where(record => record.ConversationId == projectConversationId)
            .OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var storedMessages = records
            .Select(record => JsonSerializer.Deserialize<ChatMessage>(
                record.ConversationPayload!,
                jsonOptions)!)
            .ToList();
        var storedFunctionCallIndex = storedMessages.FindIndex(message =>
            message.Contents.OfType<FunctionCallContent>().Any(content =>
                content.CallId == functionCall.CallId));
        var storedFunctionResultIndex = storedMessages.FindIndex(message =>
            message.Contents.OfType<FunctionResultContent>().Any(content =>
                content.CallId == functionCall.CallId));
        Assert.True(storedFunctionCallIndex >= 0);
        Assert.True(storedFunctionResultIndex >= 0);
        Assert.Equal(storedFunctionCallIndex + 1, storedFunctionResultIndex);
        Assert.Contains(
            storedMessages.Skip(storedFunctionResultIndex + 1),
            message => message.Text.StartsWith("### Current todo list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FunctionResultOrdering_ExternalUserBeforeFunctionResult_DoesNotReorder()
    {
        var innerClient = new FunctionCallingStubChatClient();
        using var client = new FunctionResultOrderingChatClient(innerClient);

        await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, "next request"),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "result")])
            ],
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(innerClient.Requests);
        Assert.Equal(ChatRole.User, request[0].Role);
        Assert.Equal(ChatRole.Tool, request[1].Role);
    }

    [Fact]
    public async Task FunctionResultOrdering_HistoricalResultBeforeCurrentResult_MovesCurrentResultNextToCall()
    {
        var innerClient = new FunctionCallingStubChatClient();
        using var client = new FunctionResultOrderingChatClient(innerClient);
        var contextMessage = new ChatMessage(ChatRole.User, "current todo context")
            .WithAgentRequestMessageSource(
                AgentRequestMessageSourceType.AIContextProvider,
                "TodoProvider");

        await foreach (var _ in client.GetStreamingResponseAsync(
                           [
                               CreateFunctionCallMessage("historical-call"),
                               CreateFunctionResultMessage("historical-call"),
                               CreateFunctionCallMessage("current-call"),
                               contextMessage,
                               CreateFunctionResultMessage("current-call")
                           ],
                           cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        var request = Assert.Single(innerClient.Requests);
        Assert.Collection(
            request,
            message => Assert.Equal(
                "historical-call",
                Assert.IsType<FunctionCallContent>(Assert.Single(message.Contents)).CallId),
            message => Assert.Equal(
                "historical-call",
                Assert.IsType<FunctionResultContent>(Assert.Single(message.Contents)).CallId),
            message => Assert.Equal(
                "current-call",
                Assert.IsType<FunctionCallContent>(Assert.Single(message.Contents)).CallId),
            message => Assert.Equal(
                "current-call",
                Assert.IsType<FunctionResultContent>(Assert.Single(message.Contents)).CallId),
            message => Assert.Equal(
                AgentRequestMessageSourceType.AIContextProvider,
                message.GetAgentRequestMessageSourceType()));
    }

    [Fact]
    public async Task AsAgwAgent_WithWarning_PersistsWarningBetweenUserAndAssistant()
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
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Project",
                Type = ProjectType.UserDefined,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow()
            });
            seedContext.ProjectConversations.Add(new ProjectConversation
            {
                Id = projectConversationId,
                ProjectId = projectId,
                ContextId = "context-1",
                Title = "Chat",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow(),
                UpdateBy = "tester",
                UpdateTime = TimeProvider.System.GetUtcNow()
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var historyProvider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System,
            jsonOptions);
        var agent = new StubChatClient()
            .AsAgwAgent(
                CreateDefinition(historyProvider),
                CreateCapabilities(toolWarnings: ["fallback warning"]),
                NullLoggerFactory.Instance,
                serviceProvider);
        var session = await agent.CreateSessionAsync(cancellationToken);
        historyProvider.InitializeSessionState(session, "context-1", projectId);
        var runtime = new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            projectId,
            "context-1",
            sessionStateScope: null,
            conversationHistoryWriter: historyProvider);

        await foreach (var _ in runtime.ExecuteStreamingAsync(
            new AgwUserInput
            {
                MessageId = "user-1",
                Author = "$agw",
                Contents = [new AgwTextContent { Content = "question" }]
            },
            cancellationToken))
        {
        }

        await using var verifyContext = new AgwDbContext(options);
        var records = await verifyContext.ProjectConversationChatHistories
            .Where(record => record.ConversationId == projectConversationId)
            .OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        var messages = records
            .Select(record => JsonSerializer.Deserialize<ChatMessage>(
                record.ConversationPayload!,
                jsonOptions)!)
            .ToList();

        Assert.Equal([ChatRole.User, ChatRole.System, ChatRole.Assistant], messages.Select(message => message.Role));
        Assert.Equal("tools", messages[1].AuthorName);
        Assert.Equal("fallback warning", messages[1].Text);
    }

    private static ResolvedAgentDefinition CreateDefinition() =>
        CreateDefinition(new InMemoryChatHistoryProvider());

    private static AIAgent CreateTodoMutationAgent() =>
        new TodoFunctionCallingStubChatClient()
            .AsAgwAgent(
                CreateDefinition(),
                CreateCapabilities(
                    contextProviders: [new TodoProvider()],
                    loopEvaluators: [new StopLoopEvaluator()]),
                NullLoggerFactory.Instance,
                new ServiceCollection().BuildServiceProvider());

    private static void AssertTodoSnapshots(IReadOnlyList<AgentResponseUpdate> updates)
    {
        var snapshots = updates
            .Select((update, index) => (Update: update, Index: index))
            .Where(item => IsMessageType(
                item.Update.AdditionalProperties,
                ToolMessageTypes.TodoSnapshot))
            .ToList();

        Assert.Equal(3, snapshots.Count);
        Assert.Equal(
            ["todos_add", "todos_complete", "todos_remove"],
            snapshots.Select(item => item.Update.AdditionalProperties!["toolName"]?.ToString()));

        foreach (var snapshot in snapshots)
        {
            Assert.True(snapshot.Index > 0);
            var result = Assert.Single(
                updates[snapshot.Index - 1].Contents.OfType<FunctionResultContent>());
            Assert.Equal(
                result.CallId,
                snapshot.Update.AdditionalProperties!["callId"]?.ToString());
            Assert.True(ToolStateSnapshots.RequiresSeparatePersistence(snapshot.Update));
        }

        var addedItems = GetTodoSnapshotItems(snapshots[0].Update);
        Assert.Equal(2, addedItems.GetArrayLength());
        Assert.Equal("First", addedItems[0].GetProperty("title").GetString());
        Assert.False(addedItems[0].GetProperty("isComplete").GetBoolean());
        Assert.Equal("Second", addedItems[1].GetProperty("title").GetString());
        Assert.False(addedItems[1].GetProperty("isComplete").GetBoolean());

        var completedItems = GetTodoSnapshotItems(snapshots[1].Update);
        Assert.Equal(2, completedItems.GetArrayLength());
        Assert.True(completedItems[0].GetProperty("isComplete").GetBoolean());
        Assert.False(completedItems[1].GetProperty("isComplete").GetBoolean());

        var remainingItems = GetTodoSnapshotItems(snapshots[2].Update);
        Assert.Equal(1, remainingItems.GetArrayLength());
        Assert.Equal("First", remainingItems[0].GetProperty("title").GetString());
        Assert.True(remainingItems[0].GetProperty("isComplete").GetBoolean());

        Assert.Contains(
            updates.SelectMany(update => update.Contents).OfType<FunctionResultContent>(),
            result => string.Equals(result.CallId, "todo-get-all-call", StringComparison.Ordinal));
    }

    private static JsonElement GetTodoSnapshotItems(AgentResponseUpdate update) =>
        JsonSerializer.SerializeToElement(update.AdditionalProperties!["items"]);

    private static ChatMessage CreateFunctionCallMessage(string callId) =>
        new(
            ChatRole.Assistant,
            [new FunctionCallContent(callId, "web_search", new Dictionary<string, object?>())]);

    private static ChatMessage CreateFunctionResultMessage(string callId) =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, "result")]);

    private static bool IsMessageType(
        AdditionalPropertiesDictionary? properties,
        string expectedType) =>
        properties?.TryGetValue("type", out var type) == true &&
        string.Equals(type?.ToString(), expectedType, StringComparison.Ordinal);

    private static ResolvedAgentDefinition CreateDefinition(ChatHistoryProvider chatHistoryProvider) =>
        new()
        {
            Id = "test-agent",
            Name = "Test agent",
            ModelId = "test-model",
            OpenTelemetrySourceName = "test-source",
            ChatHistoryProvider = chatHistoryProvider
        };

    private static AgentCapabilityComposition CreateCapabilities(
        IReadOnlyList<AITool>? tools = null,
        IReadOnlyList<AIContextProvider>? contextProviders = null,
        IReadOnlyList<LoopEvaluator>? loopEvaluators = null,
        IReadOnlyList<string>? toolWarnings = null,
        IReadOnlyDictionary<string, string>? toolInvocationWarnings = null) =>
        new(
            tools: tools ?? [],
            pluginSkills: [],
            warnings: [],
            contextProviders: contextProviders ?? [],
            loopEvaluators: loopEvaluators ?? [],
            autoApprovalRules: [],
            toolWarnings: toolWarnings ?? [],
            toolInvocationWarnings: toolInvocationWarnings ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            lease: new AgentResourceLease());

    private sealed class StopLoopEvaluator : LoopEvaluator
    {
        public override ValueTask<LoopEvaluation> EvaluateAsync(
            LoopContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(LoopEvaluation.Stop());
    }

    private sealed class StubChatClient : IChatClient
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }
    }

    private sealed class FunctionCallingStubChatClient : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var request = messages.Select(message => message.Clone()).ToList();
            Requests.Add(request);
            return Task.FromResult(
                request.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>()
                    .Any()
                    ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
                    : new ChatResponse(CreateFunctionCallMessage()));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var request = messages.Select(message => message.Clone()).ToList();
            Requests.Add(request);
            if (request.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .Any())
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
                yield break;
            }

            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                CreateFunctionCallMessage().Contents);
        }

        private static ChatMessage CreateFunctionCallMessage() =>
            new(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "web-search-call",
                        "web_search",
                        new Dictionary<string, object?>())
                ]);
    }

    private sealed class TodoFunctionCallingStubChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(CreateResponse(messages)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var response = CreateResponse(messages);
            yield return new ChatResponseUpdate(response.Role, response.Contents);
        }

        private static ChatMessage CreateResponse(IEnumerable<ChatMessage> messages)
        {
            var latestCallId = messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .LastOrDefault()
                ?.CallId;
            return latestCallId switch
            {
                null => CreateFunctionCall(
                    "todo-add-call",
                    "todos_add",
                    new Dictionary<string, object?>
                    {
                        ["todos"] = JsonSerializer.SerializeToElement(new[]
                        {
                            new { title = "First", description = "first item" },
                            new { title = "Second", description = "second item" }
                        })
                    }),
                "todo-add-call" => CreateFunctionCall(
                    "todo-complete-call",
                    "todos_complete",
                    new Dictionary<string, object?>
                    {
                        ["items"] = JsonSerializer.SerializeToElement(new[]
                        {
                            new { id = 1, reason = "finished" }
                        })
                    }),
                "todo-complete-call" => CreateFunctionCall(
                    "todo-remove-call",
                    "todos_remove",
                    new Dictionary<string, object?>
                    {
                        ["ids"] = JsonSerializer.SerializeToElement(new[] { 2 })
                    }),
                "todo-remove-call" => CreateFunctionCall(
                    "todo-get-all-call",
                    "todos_get_all",
                    new Dictionary<string, object?>()),
                "todo-get-all-call" => new ChatMessage(ChatRole.Assistant, "done"),
                _ => throw new InvalidOperationException($"Unexpected call ID '{latestCallId}'.")
            };
        }

        private static ChatMessage CreateFunctionCall(
            string callId,
            string name,
            IDictionary<string, object?> arguments) =>
            new(
                ChatRole.Assistant,
                [new FunctionCallContent(callId, name, arguments)]);
    }
}
