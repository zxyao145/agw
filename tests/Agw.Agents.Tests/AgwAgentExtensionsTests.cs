using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Agents.Execution.Agentflows;
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
using Microsoft.Agents.AI.Compaction;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public async Task AsAgwAgent_CompactionProvider_RunsForEveryModelCallAndSetsOutputLimit()
    {
        var function = AIFunctionFactory.Create(
            (Func<string>)(() => "search result"),
            new AIFunctionFactoryOptions { Name = "web_search" });
        var strategy = new CountingCompactionStrategy();
        var client = new FunctionCallingStubChatClient();
        var agent = client.AsAgwAgent(
            CreateDefinition(
                new InMemoryChatHistoryProvider(),
                new CompactionProvider(strategy, stateKey: "test-compaction"),
                maxOutputTokens: 64_000),
            CreateCapabilities(tools: [function]),
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider());
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await agent.RunAsync(
            [
                new ChatMessage(ChatRole.User, "previous question"),
                new ChatMessage(ChatRole.Assistant, "previous answer"),
                new ChatMessage(ChatRole.User, "search")
            ],
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, strategy.InvocationCount);
        Assert.Equal([64_000, 64_000], client.MaxOutputTokens);

        var serializedSession = await agent.SerializeSessionAsync(
            session,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("test-compaction", serializedSession.GetRawText(), StringComparison.Ordinal);
        session = await agent.DeserializeSessionAsync(
            serializedSession,
            cancellationToken: TestContext.Current.CancellationToken);

        await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "follow up")],
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, strategy.InvocationCount);
        Assert.Equal([64_000, 64_000, 64_000], client.MaxOutputTokens);
    }

    [Fact]
    public async Task ContextWindowCompactionStrategy_LongHistory_PreservesSystemAndRecentMessages()
    {
        const string toolCallId = "old-tool-call";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system instructions")
        };
        for (var turn = 0; turn < 40; turn++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"user-{turn}: {new string('u', 200)}"));
            if (turn == 2)
            {
                messages.Add(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent(toolCallId, "search", new Dictionary<string, object?>())]));
                messages.Add(new ChatMessage(
                    ChatRole.Tool,
                    [new FunctionResultContent(toolCallId, new string('r', 1_000))]));
            }

            messages.Add(new ChatMessage(ChatRole.Assistant, $"assistant-{turn}: {new string('a', 200)}"));
        }

        var compacted = (await CompactionProvider.CompactAsync(
                new ContextWindowCompactionStrategy(4_000, 1_000),
                messages,
                cancellationToken: TestContext.Current.CancellationToken))
            .ToList();

        Assert.True(compacted.Count < messages.Count);
        Assert.Contains(compacted, message => message.Role == ChatRole.System);
        Assert.Contains(compacted, message => message.Text.StartsWith("user-39:", StringComparison.Ordinal));
        var compactedCallIds = compacted
            .SelectMany(message => message.Contents)
            .Select(content => content switch
            {
                FunctionCallContent call => call.CallId,
                FunctionResultContent result => result.CallId,
                _ => null
            })
            .Where(callId => callId == toolCallId)
            .ToList();
        Assert.True(compactedCallIds.Count is 0 or 2);
    }

    [Fact]
    public async Task AsAgwAgent_Compaction_DoesNotPersistCompactedRequestMessages()
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
                ContextId = "compaction-context",
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
        var client = new RecordingResponseChatClient();
        var agent = client.AsAgwAgent(
            CreateDefinition(
                historyProvider,
                new CompactionProvider(
                    new ContextWindowCompactionStrategy(4_000, 1_000),
                    stateKey: "test-ef-compaction"),
                maxOutputTokens: 1_000),
            CreateCapabilities(),
            NullLoggerFactory.Instance,
            serviceProvider);
        var session = await agent.CreateSessionAsync(cancellationToken);
        historyProvider.InitializeSessionState(
            session,
            "compaction-context",
            projectId);
        var originalMessages = Enumerable.Range(0, 40)
            .SelectMany(turn => new[]
            {
                new ChatMessage(ChatRole.User, $"user-{turn}: {new string('u', 200)}"),
                new ChatMessage(ChatRole.Assistant, $"assistant-{turn}: {new string('a', 200)}")
            })
            .ToList();

        await agent.RunAsync(
            originalMessages,
            session,
            cancellationToken: cancellationToken);

        var modelRequest = Assert.Single(client.Requests);
        Assert.True(modelRequest.Count < originalMessages.Count);
        Assert.Contains(modelRequest, message =>
            message.Text.StartsWith("user-39:", StringComparison.Ordinal));

        await using var verifyContext = new AgwDbContext(options);
        var records = await verifyContext.ProjectConversationChatHistories
            .Where(record => record.ConversationId == projectConversationId)
            .OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        var storedMessages = records
            .Select(record => JsonSerializer.Deserialize<ChatMessage>(
                record.ConversationPayload!,
                jsonOptions)!)
            .ToList();
        Assert.Contains(storedMessages, message =>
            message.Text.StartsWith("user-0:", StringComparison.Ordinal));
        Assert.Contains(storedMessages, message =>
            message.Text.StartsWith("user-39:", StringComparison.Ordinal));
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
    public async Task AsAgwAgent_PlanModeModelRequestsPnpmFmt_DoesNotInvokeShellOrRequestApproval()
    {
        var invocationCount = 0;
        var shellFunction = AIFunctionFactory.Create(
            (Func<string, string>)(command =>
            {
                invocationCount++;
                return command;
            }),
            new AIFunctionFactoryOptions { Name = "run_shell" });
        var modeProvider = new AgentModeProvider(
            new AgentModeProviderOptions { DefaultMode = "plan" });
        var client = new IllegalShellCallChatClient();
        var agent = client.AsAgwAgent(
            CreateDefinition(),
            CreateCapabilities(
                tools: [new ApprovalRequiredAIFunction(shellFunction)],
                contextProviders: [modeProvider, new DynamicSkillToolsProvider()],
                planModeAllowedToolNames: new HashSet<string>(
                    ["mode_get", "mode_set", "load_skill", "read_skill_resource"],
                    StringComparer.OrdinalIgnoreCase)),
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider());
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "Run pnpm fmt")],
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, invocationCount);
        Assert.DoesNotContain(
            client.ExposedToolNames,
            name => string.Equals(name, "run_shell", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("load_skill", client.ExposedToolNames);
        Assert.Contains("read_skill_resource", client.ExposedToolNames);
        Assert.DoesNotContain("run_skill_script", client.ExposedToolNames);
        Assert.DoesNotContain(
            response.Messages.SelectMany(static message => message.Contents),
            static content => content is ToolApprovalRequestContent);
        var planModeResult = Assert.Single(
            response.Messages
                .SelectMany(static message => message.Contents)
                .OfType<FunctionResultContent>());
        Assert.Contains("403_0003 PlanModeToolNotAllowed", planModeResult.Result?.ToString());
    }

    [Fact]
    public async Task AsAgwAgent_ApprovedExecuteToolAfterSwitchingToPlan_DoesNotInvokeTool()
    {
        var invocationCount = 0;
        var shellFunction = AIFunctionFactory.Create(
            (Func<string>)(() =>
            {
                invocationCount++;
                return "formatted";
            }),
            new AIFunctionFactoryOptions { Name = "run_shell" });
        var modeProvider = new AgentModeProvider(
            new AgentModeProviderOptions { DefaultMode = "execute" });
        var client = new ApprovalShellCallChatClient();
        var agent = client.AsAgwAgent(
            CreateDefinition(),
            CreateCapabilities(
                tools: [new ApprovalRequiredAIFunction(shellFunction)],
                contextProviders: [modeProvider],
                planModeAllowedToolNames: new HashSet<string>(
                    ["mode_get", "mode_set"],
                    StringComparer.OrdinalIgnoreCase)),
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider());
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var firstResponse = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "Run pnpm fmt")],
            session,
            cancellationToken: TestContext.Current.CancellationToken);
        var approvalRequest = Assert.Single(
            firstResponse.Messages
                .SelectMany(static message => message.Contents)
                .OfType<ToolApprovalRequestContent>());

        await modeProvider.SetModeAsync(
            session,
            "plan",
            TestContext.Current.CancellationToken);
        var secondResponse = await agent.RunAsync(
            [
                new ChatMessage(
                    ChatRole.User,
                    [approvalRequest.CreateResponse(approved: true)])
            ],
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, invocationCount);
        var result = Assert.Single(
            secondResponse.Messages
                .SelectMany(static message => message.Contents)
                .OfType<FunctionResultContent>());
        Assert.Contains("403_0003 PlanModeToolNotAllowed", result.Result?.ToString());
        Assert.Contains("Plan mode", result.Result?.ToString(), StringComparison.OrdinalIgnoreCase);
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
    public async Task AsAgwAgent_ApprovalContinuation_WhenToolFails_KeepsFunctionResultNextToFunctionCall()
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
            (Func<string>)(() => throw new ArgumentException("old_string not found")),
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
        historyProvider.InitializeSessionState(
            session,
            "context-1",
            projectId,
            "agentflow:flow-1:node:node-1");
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
        var serializedSession = await agent.SerializeSessionAsync(
            session,
            cancellationToken: cancellationToken);
        session = await agent.DeserializeSessionAsync(
            serializedSession,
            cancellationToken: cancellationToken);
        historyProvider.InitializeSessionState(
            session,
            "context-1",
            projectId,
            "agentflow:flow-1:node:node-1");
        var continuation = AgentflowMessageTransforms.ApplyInstructions(
            [new ChatMessage(ChatRole.User, [approvalRequest.CreateAlwaysApproveToolResponse()])],
            "Follow the node instructions.");
        await foreach (var _ in agent.RunStreamingAsync(
                           continuation,
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

    private static ResolvedAgentDefinition CreateDefinition(
        ChatHistoryProvider chatHistoryProvider,
        AIContextProvider? compactionProvider = null,
        int? maxOutputTokens = null) =>
        new()
        {
            Id = "test-agent",
            Name = "Test agent",
            ModelId = "test-model",
            OpenTelemetrySourceName = "test-source",
            ChatHistoryProvider = chatHistoryProvider,
            CompactionProvider = compactionProvider,
            MaxOutputTokens = maxOutputTokens
        };

    private static AgentCapabilityComposition CreateCapabilities(
        IReadOnlyList<AITool>? tools = null,
        IReadOnlyList<AIContextProvider>? contextProviders = null,
        IReadOnlyList<LoopEvaluator>? loopEvaluators = null,
        IReadOnlySet<string>? planModeAllowedToolNames = null,
        IReadOnlyList<string>? toolWarnings = null,
        IReadOnlyDictionary<string, string>? toolInvocationWarnings = null) =>
        new(
            tools: tools ?? [],
            pluginSkills: [],
            warnings: [],
            contextProviders: contextProviders ?? [],
            loopEvaluators: loopEvaluators ?? [],
            autoApprovalRules: [],
            planModeAllowedToolNames: planModeAllowedToolNames ?? new HashSet<string>(),
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
        public List<int?> MaxOutputTokens { get; } = [];

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
            MaxOutputTokens.Add(options?.MaxOutputTokens);
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
            MaxOutputTokens.Add(options?.MaxOutputTokens);
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

    private sealed class RecordingResponseChatClient : IChatClient
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
            Requests.Add(messages.Select(message => message.Clone()).ToList());
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Requests.Add(messages.Select(message => message.Clone()).ToList());
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }
    }

    private sealed class CountingCompactionStrategy : CompactionStrategy
    {
        public CountingCompactionStrategy()
            : base(CompactionTriggers.Always)
        {
        }

        public int InvocationCount { get; private set; }

        protected override ValueTask<bool> CompactCoreAsync(
            CompactionMessageIndex index,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return ValueTask.FromResult(false);
        }
    }


    private sealed class IllegalShellCallChatClient : IChatClient
    {
        public List<string> ExposedToolNames { get; } = [];

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
            ExposedToolNames.AddRange(options?.Tools?.Select(static tool => tool.Name) ?? []);
            return Task.FromResult(new ChatResponse(CreateResponse(messages)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            ExposedToolNames.AddRange(options?.Tools?.Select(static tool => tool.Name) ?? []);
            var response = CreateResponse(messages);
            yield return new ChatResponseUpdate(response.Role, response.Contents);
        }

        private static ChatMessage CreateResponse(IEnumerable<ChatMessage> messages) =>
            messages.SelectMany(static message => message.Contents)
                .OfType<FunctionResultContent>()
                .Any()
                ? new ChatMessage(ChatRole.Assistant, "done")
                : new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "format-call",
                            "run_shell",
                            new Dictionary<string, object?> { ["command"] = "pnpm fmt" })
                    ]);
    }

    private sealed class DynamicSkillToolsProvider : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AIContext
            {
                Tools =
                [
                    CreateFunction("load_skill"),
                    CreateFunction("read_skill_resource"),
                    CreateFunction("run_skill_script")
                ]
            });

        private static AIFunction CreateFunction(string name) =>
            AIFunctionFactory.Create(
                (Func<string>)(() => name),
                new AIFunctionFactoryOptions { Name = name });
    }

    private sealed class ApprovalShellCallChatClient : IChatClient
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

        private static ChatMessage CreateResponse(IEnumerable<ChatMessage> messages) =>
            messages.SelectMany(static message => message.Contents)
                .OfType<FunctionResultContent>()
                .Any()
                ? new ChatMessage(ChatRole.Assistant, "done")
                : new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "format-call",
                            "run_shell",
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
