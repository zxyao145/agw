using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Execution.Agents.History;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Projects.Application.History;
using Agw.Projects.Application.Persistence;
using Agw.Shared;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Extensions;
using Agw.Testing;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

/// <summary>
/// AgwChatHistoryProvider 的模型历史读取、模型调用前后的写入，以及 ConversationHistoryWriter 的完整消息写入。
/// Model history reads of AgwChatHistoryProvider, its writes around a model call, and complete-message writes of ConversationHistoryWriter.
/// </summary>
public sealed class AgwChatHistoryProviderTests : IDisposable
{
    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], authenticationType: "Test")
        )
    );

    private readonly HistorySessionState _sessionState = new();

    public void Dispose() => _userScope.Dispose();

    [Fact]
    public void TryGetProjectContext_InitializedSession_ReturnsProjectAndContext()
    {
        var session = new FakeAgentSession();
        var projectId = Guid.CreateVersion7();

        _sessionState.InitializeSessionState(session, " context-1 ", projectId);

        var found = _sessionState.TryGetProjectContext(session, out var resolvedProjectId, out var contextId);

        Assert.True(found);
        Assert.Equal(projectId, resolvedProjectId);
        Assert.Equal("context-1", contextId);
    }

    [Fact]
    public void TryGetProjectContext_UninitializedSession_ReturnsFalse()
    {
        var found = _sessionState.TryGetProjectContext(new FakeAgentSession(), out var projectId, out var contextId);

        Assert.False(found);
        Assert.Equal(Guid.Empty, projectId);
        Assert.Equal(string.Empty, contextId);
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_WhenContextHasMultipleTasks_ReturnsAllContextMessages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        var otherContextId = Guid.CreateVersion7();
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.AddRange(
                CreateContext(projectConversationId, projectId, "context-1"),
                CreateContext(otherContextId, projectId, "context-2")
            );
            context.ProjectConversationChatHistories.AddRange(
                CreateRecord(projectConversationId, 0, "first"),
                CreateRecord(projectConversationId, 1, "second"),
                CreateRecord(otherContextId, 0, "other")
            );
        });
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);

        var messages = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
            cancellationToken
        );

        Assert.Equal(["first", "second"], messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_WithinOneRun_ReusesUnchangedRowsAndRereadsChangedRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        var first = CreateRecord(projectConversationId, 0, "first");
        var second = CreateRecord(projectConversationId, 1, "second");
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            context.ProjectConversationChatHistories.AddRange(first, second);
        });
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);
        var context = new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []);
        provider.Begin(new FakeAgent(), session);

        var initial = (await InvokeProvideChatHistoryAsync(provider, context, cancellationToken)).ToList();
        var repeated = (await InvokeProvideChatHistoryAsync(provider, context, cancellationToken)).ToList();
        await using (var update = database.CreateContext())
        {
            var row = await update.ProjectConversationChatHistories.SingleAsync(
                item => item.Id == second.Id,
                cancellationToken
            );
            row.ConversationPayload = JsonSerializer.Serialize(
                new ChatMessage(ChatRole.User, "second, edited"),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            );
            await update.SaveChangesAsync(cancellationToken);
        }
        var changed = (await InvokeProvideChatHistoryAsync(provider, context, cancellationToken)).ToList();
        provider.End(session);
        var outsideRun = (await InvokeProvideChatHistoryAsync(provider, context, cancellationToken)).ToList();

        Assert.NotSame(initial[0], repeated[0]);
        Assert.NotSame(initial[0].Contents, repeated[0].Contents);
        Assert.Same(initial[0].Contents[0], repeated[0].Contents[0]);
        Assert.Same(initial[1].Contents[0], repeated[1].Contents[0]);
        Assert.Same(initial[0].Contents[0], changed[0].Contents[0]);
        Assert.NotSame(initial[1].Contents[0], changed[1].Contents[0]);
        Assert.Equal(["first", "second, edited"], changed.Select(message => message.Text));
        Assert.NotSame(changed[0].Contents[0], outsideRun[0].Contents[0]);
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_WhenContextContainsControlSnapshots_ExcludesThemFromModelHistory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            context.ProjectConversationChatHistories.AddRange(
                CreateRecord(projectConversationId, 0, "normal"),
                CreateRecord(projectConversationId, 1, CreateResultMessage("summary")),
                CreateRecord(projectConversationId, 2, CreateCheckpointMessage()),
                CreateRecord(
                    projectConversationId,
                    3,
                    new ChatMessage(ChatRole.User, "user memory").WithAgentRequestMessageSource(
                        AgentRequestMessageSourceType.AIContextProvider,
                        ConversationHistoryMetadata.UserMemorySourceId
                    )
                )
            );
        });
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);

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
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            context.ProjectConversationChatHistories.AddRange(
                CreateRecord(projectConversationId, 0, "normal"),
                CreateRecord(projectConversationId, 1, CreateToolBlockMessage(AgwMessageTypes.ToolTodoSnapshot))
            );
        });
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);

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
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        var approvalRequest = new ToolApprovalRequestContent(
            "approval-1",
            new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>())
        );
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            context.ProjectConversationChatHistories.Add(
                CreateRecord(projectConversationId, 0, new ChatMessage(ChatRole.Assistant, [approvalRequest]))
            );
        });
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);

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
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            context.ProjectConversationChatHistories.AddRange(
                CreateRecord(
                    projectConversationId,
                    0,
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent("matched-call", "read_file", new Dictionary<string, object?>())]
                    )
                ),
                CreateRecord(
                    projectConversationId,
                    1,
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent("matched-call", "matched result")])
                ),
                CreateRecord(projectConversationId, 2, new ChatMessage(ChatRole.Assistant, "completed")),
                CreateRecord(
                    projectConversationId,
                    3,
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent("orphaned-call", "orphaned result")])
                ),
                CreateRecord(projectConversationId, 4, new ChatMessage(ChatRole.User, "continue"))
            );
        });
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);

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
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            context.ProjectConversationChatHistories.AddRange(
                CreateRecord(
                    projectConversationId,
                    0,
                    new ChatMessage(
                        ChatRole.Assistant,
                        [
                            new TextContent("working"),
                            new FunctionCallContent("matched-call", "read_file", new Dictionary<string, object?>()),
                            new FunctionCallContent("missing-call", "write_file", new Dictionary<string, object?>()),
                        ]
                    )
                ),
                CreateRecord(
                    projectConversationId,
                    1,
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent("matched-call", "matched result")])
                ),
                CreateRecord(
                    projectConversationId,
                    2,
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent("interrupted-call", "read_file", new Dictionary<string, object?>())]
                    )
                ),
                CreateRecord(projectConversationId, 3, new ChatMessage(ChatRole.User, "continue"))
            );
        });
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);

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
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            context.ProjectConversationChatHistories.Add(
                CreateRecord(
                    projectConversationId,
                    0,
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent("call-1", "todos_add", new Dictionary<string, object?>())]
                    )
                )
            );
        });
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);
        var currentToolResult = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "todo added")]);
        var contextMessage = new ChatMessage(ChatRole.User, "current todo context").WithAgentRequestMessageSource(
            AgentRequestMessageSourceType.AIContextProvider,
            "AgwTodoProvider"
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
    public async Task ProvideChatHistoryAsync_WhenCallsSpanSeparateAssistantMessages_PreservesAnsweredCall()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            context.ProjectConversationChatHistories.AddRange(
                CreateRecord(projectConversationId, 0, new ChatMessage(ChatRole.Assistant, "loading the skill")),
                CreateRecord(
                    projectConversationId,
                    1,
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent("answered-call", "load_skill", new Dictionary<string, object?>())]
                    )
                ),
                CreateRecord(
                    projectConversationId,
                    2,
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent("awaiting-call", "run_shell", new Dictionary<string, object?>())]
                    )
                )
            );
        });
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);
        var currentToolResult = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("answered-call", "skill loaded")]
        );
        var approval = new ChatMessage(
            ChatRole.User,
            [
                new ToolApprovalResponseContent(
                    "awaiting-call",
                    approved: true,
                    new FunctionCallContent("awaiting-call", "run_shell", new Dictionary<string, object?>())
                ),
            ]
        );

        var messages = (
            await InvokeProvideChatHistoryAsync(
                provider,
                new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, [approval, currentToolResult]),
                cancellationToken
            )
        ).ToList();

        Assert.Collection(
            messages,
            message => Assert.Equal("loading the skill", message.Text),
            message =>
                Assert.Equal(
                    "answered-call",
                    Assert.IsType<FunctionCallContent>(Assert.Single(message.Contents)).CallId
                )
        );
    }

    [Fact]
    public async Task ProvideChatHistoryAsync_WhenToolMessageHasPortableContent_PreservesIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            context.ProjectConversationChatHistories.Add(
                CreateRecord(projectConversationId, 0, new ChatMessage(ChatRole.Tool, "portable tool content"))
            );
        });
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);

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
        await using var database = await Database.CreateAsync();
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await database.SeedAsync(context => context.Projects.Add(CreateProject(projectId)));
        var provider = CreateProvider(database);
        var firstSession = new FakeAgentSession();
        var secondSession = new FakeAgentSession();
        var unscopedSession = new FakeAgentSession();
        _sessionState.InitializeSessionState(firstSession, "context-1", projectId, "agentflow:flow:node-a");
        _sessionState.InitializeSessionState(secondSession, "context-1", projectId, "agentflow:flow:node-b");
        _sessionState.InitializeSessionState(unscopedSession, "context-1", projectId);

        await RunModelCallAsync(provider, firstSession, [new ChatMessage(ChatRole.User, "first private history")], []);
        await RunModelCallAsync(provider, unscopedSession, [new ChatMessage(ChatRole.User, "unscoped history")], []);
        await RunModelCallAsync(
            provider,
            secondSession,
            [new ChatMessage(ChatRole.User, "second private history")],
            []
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

        await using var verifyContext = database.CreateContext();
        Assert.Single(await verifyContext.ProjectConversations.ToListAsync(cancellationToken));
        var records = await verifyContext
            .ProjectConversationChatHistories.OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        Assert.Equal(
            ["agentflow:flow:node-a", null, "agentflow:flow:node-b"],
            records.Select(record => record.HistoryScope)
        );
        Assert.All(records, record => Assert.False(record.Metadata?.ContainsKey("historyScope") == true));
    }

    [Fact]
    public async Task ModelCall_NodeScopedSession_PersistsDisplayMetadataOnlyOnResponses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        await database.SeedAsync(context => context.Projects.Add(CreateProject(projectId)));
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(
            session,
            "context-1",
            projectId,
            "agentflow:flow:node-a",
            "  Review Node  "
        );
        var displayOnlyMessage = new ChatMessage(ChatRole.System, "working") { MessageId = "progress-1" };
        ConversationHistoryMetadata.ExcludeFromModelHistory(displayOnlyMessage);

        await RunModelCallAsync(
            provider,
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
            ],
            new FakeAgent("  general-agent  ")
        );

        await using var verifyContext = database.CreateContext();
        var records = await verifyContext
            .ProjectConversationChatHistories.OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        var messages = records.Select(record => record.ToChatMessage()!).ToList();

        Assert.Equal(
            ["user-1", "assistant-1", "assistant-2", "progress-1"],
            messages.Select(message => message.AdditionalProperties!["sourceMessageId"]?.ToString())
        );
        Assert.Equal(records.Select(record => record.Id.ToString("D")), messages.Select(message => message.MessageId));
        Assert.NotNull(MessageTimestampMetadata.GetCreatedAt(messages[0].AdditionalProperties));
        Assert.False(messages[0].AdditionalProperties!.ContainsKey("agentName"));
        Assert.False(messages[0].AdditionalProperties!.ContainsKey("nodeName"));
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
        Assert.All(records, record => Assert.Equal("agentflow:flow:node-a", record.HistoryScope));

        var modelHistory = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
            cancellationToken
        );
        Assert.Equal(["question", "answer", "nested answer"], modelHistory.Select(message => message.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendAsync_ExistingConversation_PreservesConversationAudit(bool hasUpdateTime)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 9, 7, 1, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);
        await using var database = await Database.CreateAsync(
            new EntityModifierInterceptor(new TestAuditUserIdProvider(), clock)
        );
        var projectId = Guid.CreateVersion7();
        var conversation = CreateContext(Guid.CreateVersion7(), projectId, "context-1");
        conversation.CreateTime = now.AddHours(-2);
        conversation.UpdateTime = hasUpdateTime ? now.AddHours(-1) : null;
        conversation.UpdateBy = "previous-writer";
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.Add(conversation);
        });
        var writer = CreateWriter(database, clock);

        // Act
        await writer.AppendAsync(
            projectId,
            conversation.ContextId,
            [new ChatMessage(ChatRole.User, "question"), new ChatMessage(ChatRole.Assistant, "answer")],
            cancellationToken
        );

        // Assert
        await using var verifyContext = database.CreateContext();
        var persistedConversation = await verifyContext.ProjectConversations.SingleAsync(cancellationToken);
        Assert.Equal(conversation.UpdateTime, persistedConversation.UpdateTime);
        Assert.Equal(conversation.UpdateBy, persistedConversation.UpdateBy);
        var records = await verifyContext
            .ProjectConversationChatHistories.OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        Assert.Equal(["question", "answer"], records.Select(record => record.GetText()));
        Assert.All(records, record => Assert.Equal(now, record.UpdateTime));
    }

    [Fact]
    public async Task AppendAsync_WhenMessagesContainBlankText_PreservesProtocolReasoning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        await database.SeedAsync(context => context.Projects.Add(CreateProject(projectId)));
        var writer = CreateWriter(database);

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
                new ChatMessage(
                    ChatRole.Assistant,
                    [new TextReasoningContent("") { ProtectedData = "signature-only" }]
                ),
                new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("")]),
            ],
            cancellationToken
        );

        var persisted = await database.ReadMessagesAsync();
        Assert.Collection(
            persisted,
            message => Assert.Equal("\t", Assert.IsType<TextReasoningContent>(Assert.Single(message.Contents)).Text),
            message =>
            {
                Assert.Equal(" ", Assert.IsType<TextReasoningContent>(message.Contents[0]).Text);
                Assert.Equal("call-1", Assert.IsType<FunctionCallContent>(message.Contents[1]).CallId);
                Assert.Equal(2, message.Contents.Count);
            },
            message => Assert.Equal("hello", Assert.IsType<TextContent>(Assert.Single(message.Contents)).Text),
            message =>
                Assert.Equal(
                    "kept reasoning",
                    Assert.IsType<TextReasoningContent>(Assert.Single(message.Contents)).Text
                ),
            message =>
                Assert.Equal(
                    "signature-only",
                    Assert.IsType<TextReasoningContent>(Assert.Single(message.Contents)).ProtectedData
                ),
            message => Assert.Equal("", Assert.IsType<TextReasoningContent>(Assert.Single(message.Contents)).Text)
        );
    }

    [Fact]
    public async Task AppendAsync_EmptyToolBlockState_PersistsForConversationHistory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        await database.SeedAsync(context => context.Projects.Add(CreateProject(projectId)));
        var writer = CreateWriter(database);

        await writer.AppendAsync(
            projectId,
            "context-1",
            [CreateToolBlockMessage(AgwMessageTypes.ToolTodoSnapshot)],
            cancellationToken
        );

        var persisted = Assert.Single(await database.ReadMessagesAsync());
        Assert.Equal(AgwMessageTypes.ToolTodoSnapshot, persisted.AdditionalProperties!["type"]?.ToString());
        Assert.Equal(string.Empty, Assert.IsType<TextContent>(Assert.Single(persisted.Contents)).Text);
    }

    [Fact]
    public async Task AppendAsync_ResultMessage_PersistsItForConversationHistory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        await database.SeedAsync(context => context.Projects.Add(CreateProject(projectId)));
        var writer = CreateWriter(database);

        await writer.AppendAsync(projectId, "context-1", [CreateResultMessage("summary")], cancellationToken);

        var persisted = Assert.Single(await database.ReadMessagesAsync());
        Assert.Equal("summary", Assert.IsType<TextContent>(Assert.Single(persisted.Contents)).Text);
        Assert.Equal("result", persisted.AdditionalProperties!["type"]?.ToString());
    }

    [Fact]
    public async Task AppendAsync_ChineseText_PersistsUnescapedCharacters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        await database.SeedAsync(context => context.Projects.Add(CreateProject(projectId)));
        var writer = CreateWriter(database);

        await writer.AppendAsync(
            projectId,
            "context-1",
            [new ChatMessage(ChatRole.Assistant, "中文内容") { MessageId = "assistant-1" }],
            cancellationToken
        );

        await using var verifyContext = database.CreateContext();
        var record = await verifyContext.ProjectConversationChatHistories.SingleAsync(cancellationToken);
        Assert.Contains("中文内容", record.ConversationPayload);
        Assert.DoesNotContain("\\u", record.ConversationPayload);
    }

    [Fact]
    public async Task AppendAsync_WithLegacyUppercaseGuidContext_LeavesOldContextUntouched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var contextGuid = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.Add(
                CreateContext(projectConversationId, projectId, contextGuid.ToString("D").ToUpperInvariant())
            );
        });
        var writer = CreateWriter(database);

        await writer.AppendAsync(
            projectId,
            contextGuid.Normalize(),
            [new ChatMessage(ChatRole.User, "continue")],
            cancellationToken
        );

        await using var verifyContext = database.CreateContext();
        var contexts = await verifyContext.ProjectConversations.ToListAsync(cancellationToken);
        Assert.Equal(2, contexts.Count);
        Assert.Equal(
            contextGuid.ToString("D").ToUpperInvariant(),
            contexts.Single(item => item.Id == projectConversationId).ContextId
        );
        var current = contexts.Single(item => item.Id != projectConversationId);
        Assert.Equal(contextGuid.Normalize(), current.ContextId);
        Assert.Equal(
            current.Id,
            (await verifyContext.ProjectConversationChatHistories.SingleAsync(cancellationToken)).ConversationId
        );
    }

    [Fact]
    public async Task ModelCall_EachCallUsesItsOwnProducer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
            context.ProjectConversationChatHistories.Add(CreateRecord(projectConversationId, 0, "existing"));
        });
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);

        await RunModelCallAsync(
            provider,
            session,
            [new ChatMessage(ChatRole.User, "new")],
            [new ChatMessage(ChatRole.Assistant, "response")]
        );
        await RunModelCallAsync(provider, session, [new ChatMessage(ChatRole.User, "next")], []);

        await using var verifyContext = database.CreateContext();
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
    public async Task ModelCall_WithPreludeMessage_PersistsItBetweenRequestAndResponse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var projectConversationId = Guid.CreateVersion7();
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.Add(CreateContext(projectConversationId, projectId, "context-1"));
        });
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);
        ConversationHistoryPrelude.Set(
            session,
            [
                new ChatMessage(ChatRole.System, "fallback warning")
                {
                    MessageId = "warning-1",
                    AuthorName = "tools",
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["type"] = AgwMessageTypes.ToolWarning,
                    },
                },
            ]
        );
        var transientContext = new ChatMessage(ChatRole.User, "private memory") { MessageId = "memory-1" };
        ConversationHistoryMetadata.ExcludeFromPersistence(transientContext);

        await RunModelCallAsync(
            provider,
            session,
            [new ChatMessage(ChatRole.User, "question") { MessageId = "user-1" }, transientContext],
            [new ChatMessage(ChatRole.Assistant, "answer") { MessageId = "assistant-1" }]
        );

        await using var verifyContext = database.CreateContext();
        var records = await verifyContext
            .ProjectConversationChatHistories.Where(record => record.ConversationId == projectConversationId)
            .OrderBy(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        var messages = records.Select(record => record.ToChatMessage()!).ToList();

        Assert.Equal(
            ["user-1", "warning-1", "assistant-1"],
            messages.Select(message => message.AdditionalProperties!["sourceMessageId"]?.ToString())
        );
        Assert.Equal([0, 1, 2], records.Select(record => record.ConversationSequence));
        Assert.Empty(ConversationHistoryPrelude.Take(session));
    }

    [Fact]
    public async Task ModelCall_WhenExistingContextUsesFallbackTitle_UpdatesTitleFromFirstUserMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        await database.SeedAsync(context =>
        {
            context.Projects.Add(CreateProject(projectId));
            context.ProjectConversations.Add(
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
        });
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);
        var message = new ChatMessage(ChatRole.User, "Draft the launch plan")
        {
            MessageId = Guid.CreateVersion7().ToString(),
            AuthorName = "tester",
        };

        await RunModelCallAsync(provider, session, [message], []);

        await using var verifyContext = database.CreateContext();
        var projectConversation = await verifyContext.ProjectConversations.SingleAsync(cancellationToken);
        Assert.Equal("Draft the launch plan", projectConversation.Title);
    }

    [Fact]
    public async Task ModelCall_DataContent_PersistsAndRestoresImage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        await database.SeedAsync(context => context.Projects.Add(CreateProject(projectId)));
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);
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

        await RunModelCallAsync(provider, session, [request], []);

        var history = await InvokeProvideChatHistoryAsync(
            provider,
            new ChatHistoryProvider.InvokingContext(new FakeAgent(), session, []),
            cancellationToken
        );
        var restored = Assert.Single(history);
        var image = Assert.IsType<DataContent>(restored.Contents[0]);

        Assert.Equal("user-with-image", restored.AdditionalProperties!["sourceMessageId"]?.ToString());
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Data.ToArray());
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("screen.png", image.Name);
        Assert.Equal("describe this", Assert.IsType<TextContent>(restored.Contents[1]).Text);
    }

    [Fact]
    public async Task ModelCall_PersistsTargetMetadataFromRequestMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        await database.SeedAsync(context => context.Projects.Add(CreateProject(projectId)));
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);
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

        await RunModelCallAsync(provider, session, [message], []);

        await using var verifyContext = database.CreateContext();
        var record = await verifyContext.ProjectConversationChatHistories.SingleAsync(cancellationToken);
        Assert.NotNull(record.Metadata);
        Assert.Equal("agent", record.Metadata!["targetType"].GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", record.Metadata["targetId"].GetString());
    }

    [Fact]
    public async Task ModelCall_HandoffAndCurrentInput_PersistsOnlyCurrentInputAndCursor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.CreateVersion7();
        await database.SeedAsync(context => context.Projects.Add(CreateProject(projectId)));
        var provider = CreateProvider(database);
        var session = new FakeAgentSession();
        _sessionState.InitializeSessionState(session, "context-1", projectId);
        var handoff = new ChatMessage(ChatRole.Assistant, "previous plan");
        ConversationHandoffMetadata.MarkHandoffMessage(handoff);
        var current = new ChatMessage(ChatRole.User, "implement it")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ConversationHandoffMetadata.ThroughSequenceKey] = 17L,
            },
        };

        await RunModelCallAsync(provider, session, [handoff, current], []);

        await using var verifyContext = database.CreateContext();
        var record = await verifyContext.ProjectConversationChatHistories.SingleAsync(cancellationToken);
        Assert.Equal("implement it", record.GetText());
        Assert.Equal(17, record.Metadata![ConversationHandoffMetadata.ThroughSequenceKey].GetInt64());
    }

    [Fact]
    public async Task AppendAsync_ConcurrentCalls_AssignsUniqueOrderedSequences()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync();
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await database.SeedAsync(context => context.Projects.Add(CreateProject(projectId)));
        var writer = CreateWriter(database);
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

        await using var verificationContext = database.CreateContext();
        var context = await verificationContext.ProjectConversations.SingleAsync(cancellationToken);
        Assert.Equal("tester", context.CreateBy);
        Assert.Equal("tester", context.UpdateBy);
        var sequences = await verificationContext
            .ProjectConversationChatHistories.Where(record => record.ConversationId == context.Id)
            .OrderBy(record => record.ConversationSequence)
            .Select(record => record.ConversationSequence)
            .ToListAsync(cancellationToken);
        Assert.Equal(Enumerable.Range(0, 12).Select(static value => (long?)value), sequences);
    }

    private AgwChatHistoryProvider CreateProvider(Database database) =>
        AgwChatHistoryProvider.Create(
            database.CreateStore(TimeProvider.System),
            _sessionState,
            TimeProvider.System,
            EngineKind.Maf,
            structuredResult: false
        );

    private static ConversationHistoryWriter CreateWriter(Database database, TimeProvider? clock = null) =>
        new(database.CreateStore(clock ?? TimeProvider.System), clock ?? TimeProvider.System);

    /// <summary>
    /// 一次没有外层记录的模型调用：调用前写入请求中的输入，调用后写入完整响应。
    /// One model call without an outer recording: the request's input is written before the call and the complete response after it.
    /// </summary>
    private static async Task RunModelCallAsync(
        AgwChatHistoryProvider provider,
        AgentSession session,
        IReadOnlyList<ChatMessage> request,
        IReadOnlyList<ChatMessage> response,
        AIAgent? agent = null
    )
    {
        agent ??= new FakeAgent();
        await provider.InvokingAsync(
            new ChatHistoryProvider.InvokingContext(agent, session, request),
            TestContext.Current.CancellationToken
        );
        await provider.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(agent, session, request, response),
            TestContext.Current.CancellationToken
        );
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
        long sequence,
        string text
    ) => CreateRecord(projectConversationId, sequence, new ChatMessage(ChatRole.User, text));

    private static ProjectConversationChatHistory CreateRecord(
        Guid projectConversationId,
        long sequence,
        ChatMessage message
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ConversationId = projectConversationId,
            TaskId = Guid.CreateVersion7(),
            Status = TaskExecutionStatus.Succeeded,
            ConversationSequence = sequence,
            ConversationPayload = JsonSerializer.Serialize(
                message,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            ),
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
        AgwChatHistoryProvider provider,
        ChatHistoryProvider.InvokingContext context,
        CancellationToken cancellationToken
    )
    {
        var method = typeof(AgwChatHistoryProvider).GetMethod(
            "ProvideChatHistoryAsync",
            BindingFlags.NonPublic | BindingFlags.Instance
        );

        Assert.NotNull(method);

        var valueTask = (ValueTask<IEnumerable<ChatMessage>>)method!.Invoke(provider, [context, cancellationToken])!;
        return await valueTask;
    }

    /// <summary>
    /// 内存中的 SQLite 数据库；连接在整个测试期间保持打开。
    /// An in-memory SQLite database whose connection stays open for the whole test.
    /// </summary>
    private sealed class Database : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly ServiceProvider _services;

        private Database(SqliteConnection connection, DbContextOptions<AgwDbContext> options)
        {
            _connection = connection;
            _options = options;
            _services = new ServiceCollection()
                .AddScoped<IProjectsDbContext>(_ => new AgwDbContext(_options))
                .BuildServiceProvider();
        }

        public static async Task<Database> CreateAsync(IInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var builder = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention();
            if (interceptor != null)
                builder.AddInterceptors(interceptor);
            var database = new Database(connection, builder.Options);
            await using var context = database.CreateContext();
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return database;
        }

        public AgwDbContext CreateContext() => new(_options);

        public async Task SeedAsync(Action<AgwDbContext> seed)
        {
            await using var context = CreateContext();
            seed(context);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public ConversationHistoryStore CreateStore(TimeProvider clock) =>
            new(
                _services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ConversationHistoryStore>.Instance,
                clock
            );

        public async Task<List<ChatMessage>> ReadMessagesAsync()
        {
            await using var context = CreateContext();
            var records = await context
                .ProjectConversationChatHistories.OrderBy(record => record.ConversationSequence)
                .ToListAsync(TestContext.Current.CancellationToken);
            return records.Select(record => record.ToChatMessage()!).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
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

    private sealed class TestAuditUserIdProvider : Agw.Shared.Data.Abstractions.IEntityAuditUserIdProvider
    {
        public string GetUserId() => UserInfoUtil.UserId ?? "tester";
    }

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
