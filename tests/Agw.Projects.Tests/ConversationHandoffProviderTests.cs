using System.Text.Json;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Tests;

public class ConversationHandoffProviderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CreateAsync_LegacyCurrentTargetWithoutCursor_RecoversPreviousPlan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var options = CreateOptions(connection);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var generalAgentId = Guid.CreateVersion7();
        var codingAgentflowId = Guid.CreateVersion7();
        await SeedAsync(
            options,
            projectId,
            conversationId,
            cancellationToken,
            CreateRecord(
                conversationId,
                0,
                CreateUserMessage("prepare the implementation plan", "general-user"),
                CreateMetadata(AgentRuntimeType.Agent, generalAgentId)
            ),
            CreateRecord(
                conversationId,
                1,
                new ChatMessage(
                    ChatRole.System,
                    [new TextReasoningContent("private reasoning"), new TextContent("public plan")]
                )
                {
                    MessageId = "general-plan",
                    AuthorName = Constants.DefaultAgentAuthor,
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = "result" },
                }
            ),
            CreateRecord(
                conversationId,
                2,
                CreateUserMessage("start implementation", "coding-user"),
                CreateMetadata(historyScope: CreateScope(codingAgentflowId))
            ),
            CreateRecord(
                conversationId,
                3,
                new ChatMessage(ChatRole.Assistant, "Which plan should I implement?") { MessageId = "coding-response" },
                CreateMetadata(historyScope: CreateScope(codingAgentflowId))
            )
        );

        await using var dbContext = new AgwDbContext(options);
        var provider = CreateProvider(dbContext);

        var handoff = await provider.CreateAsync(
            conversationId,
            AgentRuntimeType.Agentflow,
            codingAgentflowId,
            cancellationToken
        );

        Assert.Equal(3, handoff.ThroughSequence);
        Assert.Equal(
            ["prepare the implementation plan", "public plan"],
            handoff.Messages.Select(message => message.Text)
        );
        Assert.Equal([ChatRole.User, ChatRole.Assistant], handoff.Messages.Select(message => message.Role));
        Assert.All(handoff.Messages, message => Assert.True(ConversationHandoffMetadata.IsHandoffMessage(message)));
        Assert.DoesNotContain(
            handoff.Messages.SelectMany(message => message.Contents),
            content => content is TextReasoningContent
        );
    }

    [Fact]
    public async Task CreateAsync_CurrentTargetCursor_ReturnsOnlyNewOtherTargetMessages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var options = CreateOptions(connection);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var generalAgentId = Guid.CreateVersion7();
        var codingAgentflowId = Guid.CreateVersion7();
        await SeedAsync(
            options,
            projectId,
            conversationId,
            cancellationToken,
            CreateRecord(
                conversationId,
                0,
                CreateUserMessage("old request", "old-user"),
                CreateMetadata(AgentRuntimeType.Agent, generalAgentId)
            ),
            CreateRecord(conversationId, 1, CreateAssistantMessage("old response", "old-response")),
            CreateRecord(
                conversationId,
                2,
                CreateUserMessage("coding turn", "coding-user"),
                CreateMetadata(
                    AgentRuntimeType.Agentflow,
                    codingAgentflowId,
                    CreateScope(codingAgentflowId),
                    throughSequence: 1
                )
            ),
            CreateRecord(
                conversationId,
                3,
                CreateUserMessage("new request", "new-user"),
                CreateMetadata(AgentRuntimeType.Agent, generalAgentId)
            ),
            CreateRecord(conversationId, 4, CreateAssistantMessage("new response", "new-response"))
        );

        await using var dbContext = new AgwDbContext(options);
        var provider = CreateProvider(dbContext);

        var handoff = await provider.CreateAsync(
            conversationId,
            AgentRuntimeType.Agentflow,
            codingAgentflowId,
            cancellationToken
        );

        Assert.Equal(4, handoff.ThroughSequence);
        Assert.Equal(["new request", "new response"], handoff.Messages.Select(message => message.Text));

        dbContext.ProjectConversationChatHistories.Add(
            CreateRecord(
                conversationId,
                5,
                CreateUserMessage("continue coding", "coding-user-2"),
                CreateMetadata(
                    AgentRuntimeType.Agentflow,
                    codingAgentflowId,
                    CreateScope(codingAgentflowId),
                    throughSequence: 4
                )
            )
        );
        await dbContext.SaveChangesAsync(cancellationToken);

        var repeated = await provider.CreateAsync(
            conversationId,
            AgentRuntimeType.Agentflow,
            codingAgentflowId,
            cancellationToken
        );

        Assert.Empty(repeated.Messages);
        Assert.Equal(5, repeated.ThroughSequence);
    }

    [Fact]
    public async Task CreateAsync_StandaloneTarget_IncludesScopedAndHiddenResultButNotVisibleUnscopedHistory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var options = CreateOptions(connection);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var firstAgentId = Guid.CreateVersion7();
        var currentAgentId = Guid.CreateVersion7();
        var agentflowId = Guid.CreateVersion7();
        await SeedAsync(
            options,
            projectId,
            conversationId,
            cancellationToken,
            CreateRecord(
                conversationId,
                0,
                CreateUserMessage("visible request", "visible-user"),
                CreateMetadata(AgentRuntimeType.Agent, firstAgentId)
            ),
            CreateRecord(conversationId, 1, CreateAssistantMessage("already visible", "visible-response")),
            CreateRecord(
                conversationId,
                2,
                new ChatMessage(ChatRole.System, "hidden final result")
                {
                    MessageId = "result",
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = "result" },
                }
            ),
            CreateRecord(
                conversationId,
                3,
                CreateUserMessage("agentflow request", "flow-user"),
                CreateMetadata(AgentRuntimeType.Agentflow, agentflowId, CreateScope(agentflowId))
            ),
            CreateRecord(
                conversationId,
                4,
                CreateAssistantMessage("agentflow response", "flow-response"),
                CreateMetadata(historyScope: CreateScope(agentflowId))
            )
        );

        await using var dbContext = new AgwDbContext(options);
        var handoff = await CreateProvider(dbContext)
            .CreateAsync(conversationId, AgentRuntimeType.Agent, currentAgentId, cancellationToken);

        Assert.Equal(
            ["hidden final result", "agentflow request", "agentflow response"],
            handoff.Messages.Select(message => message.Text)
        );
    }

    [Fact]
    public async Task CreateAsync_DuplicateMessageIdsAndOversizedLatestMessage_DeduplicatesAndKeepsLatestComplete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var options = CreateOptions(connection);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var sourceAgentId = Guid.CreateVersion7();
        var targetAgentflowId = Guid.CreateVersion7();
        var oversizedPlan = new string('p', 33_000);
        await SeedAsync(
            options,
            projectId,
            conversationId,
            cancellationToken,
            CreateRecord(
                conversationId,
                0,
                CreateUserMessage("duplicated", "duplicate-user"),
                CreateMetadata(AgentRuntimeType.Agent, sourceAgentId)
            ),
            CreateRecord(
                conversationId,
                1,
                CreateUserMessage("duplicated", "duplicate-user"),
                CreateMetadata(AgentRuntimeType.Agent, sourceAgentId)
            ),
            CreateRecord(conversationId, 2, CreateAssistantMessage(oversizedPlan, "large-plan"))
        );

        await using var dbContext = new AgwDbContext(options);
        var handoff = await CreateProvider(dbContext)
            .CreateAsync(conversationId, AgentRuntimeType.Agentflow, targetAgentflowId, cancellationToken);

        var message = Assert.Single(handoff.Messages);
        Assert.Equal("large-plan", message.MessageId);
        Assert.Equal(oversizedPlan, message.Text);
    }

    [Fact]
    public async Task CreateAsync_DuplicateMessageIds_KeepsLatestRecord()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var options = CreateOptions(connection);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var sourceAgentId = Guid.CreateVersion7();
        await SeedAsync(
            options,
            projectId,
            conversationId,
            cancellationToken,
            CreateRecord(
                conversationId,
                0,
                CreateUserMessage("request", "user-message"),
                CreateMetadata(AgentRuntimeType.Agent, sourceAgentId)
            ),
            CreateRecord(
                conversationId,
                1,
                CreateUserMessage("partial", "shared-response"),
                CreateMetadata(AgentRuntimeType.Agent, sourceAgentId)
            ),
            CreateRecord(conversationId, 2, CreateAssistantMessage("complete", "shared-response"))
        );

        await using var dbContext = new AgwDbContext(options);
        var handoff = await CreateProvider(dbContext)
            .CreateAsync(conversationId, AgentRuntimeType.Agentflow, Guid.CreateVersion7(), cancellationToken);

        Assert.Equal(["request", "complete"], handoff.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task CreateAsync_PrivateControlAndToolMessages_ReturnsOnlyPublicTextAndResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var options = CreateOptions(connection);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var sourceAgentId = Guid.CreateVersion7();
        await SeedAsync(
            options,
            projectId,
            conversationId,
            cancellationToken,
            CreateRecord(
                conversationId,
                0,
                CreateUserMessage("request", "user-message"),
                CreateMetadata(AgentRuntimeType.Agent, sourceAgentId)
            ),
            CreateRecord(
                conversationId,
                1,
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new TextReasoningContent("private reasoning"),
                        new FunctionCallContent("call-1", "private-tool"),
                        new TextContent("public explanation"),
                    ]
                )
                {
                    MessageId = "mixed-response",
                }
            ),
            CreateRecord(
                conversationId,
                2,
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "private result")])
                {
                    MessageId = "tool-result",
                }
            ),
            CreateRecord(
                conversationId,
                3,
                CreateTypedAssistantMessage("saved checkpoint", "checkpoint", "agentflow-checkpoint")
            ),
            CreateRecord(
                conversationId,
                4,
                CreateTypedAssistantMessage("pending approval", "approval", "tool-approval-request")
            ),
            CreateRecord(
                conversationId,
                5,
                CreateTypedAssistantMessage("tool state", "tool-state", ToolMessageTypes.ModeStatus)
            ),
            CreateRecord(conversationId, 6, CreateAssistantMessage(" \t\r\n", "blank")),
            CreateRecord(conversationId, 7, CreateTypedAssistantMessage("final plan", "result", "result")),
            CreateRecord(conversationId, 8, CreateDisplayOnlyMessage("progress", "display-only"))
        );

        await using var dbContext = new AgwDbContext(options);
        var handoff = await CreateProvider(dbContext)
            .CreateAsync(conversationId, AgentRuntimeType.Agentflow, Guid.CreateVersion7(), cancellationToken);

        Assert.Equal(["request", "public explanation", "final plan"], handoff.Messages.Select(message => message.Text));
        Assert.All(
            handoff.Messages.SelectMany(message => message.Contents),
            content => Assert.IsType<TextContent>(content)
        );
    }

    private static ConversationHandoffProvider CreateProvider(AgwDbContext dbContext) =>
        new(new EfRepository<ProjectConversationChatHistory>(dbContext));

    private static async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).UseSnakeCaseNamingConvention().Options;

    private static async Task SeedAsync(
        DbContextOptions<AgwDbContext> options,
        Guid projectId,
        Guid conversationId,
        CancellationToken cancellationToken,
        params ProjectConversationChatHistory[] records
    )
    {
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        dbContext.Projects.Add(
            new Project
            {
                Id = projectId,
                Name = "handoff-project",
                Type = ProjectType.UserDefined,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow(),
            }
        );
        dbContext.ProjectConversations.Add(
            new ProjectConversation
            {
                Id = conversationId,
                ProjectId = projectId,
                ContextId = Guid.CreateVersion7().ToString(),
                Title = "handoff",
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow(),
                UpdateBy = "tester",
                UpdateTime = TimeProvider.System.GetUtcNow(),
            }
        );
        dbContext.ProjectConversationChatHistories.AddRange(records);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static ProjectConversationChatHistory CreateRecord(
        Guid conversationId,
        long sequence,
        ChatMessage message,
        Dictionary<string, JsonElement>? metadata = null
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversationId,
            TaskId = Guid.CreateVersion7(),
            Status = TaskExecutionStatus.Succeeded,
            ConversationSequence = sequence,
            ConversationPayload = JsonSerializer.Serialize(message, JsonOptions),
            Metadata = metadata,
            CreateTime = TimeProvider.System.GetUtcNow(),
            UpdateTime = TimeProvider.System.GetUtcNow(),
        };

    private static ChatMessage CreateUserMessage(string text, string messageId) =>
        new(ChatRole.User, text) { MessageId = messageId, AuthorName = Constants.DefaultInputAuthor };

    private static ChatMessage CreateAssistantMessage(string text, string messageId) =>
        new(ChatRole.Assistant, text) { MessageId = messageId, AuthorName = Constants.DefaultAgentAuthor };

    private static ChatMessage CreateTypedAssistantMessage(string text, string messageId, string type) =>
        new(ChatRole.Assistant, text)
        {
            MessageId = messageId,
            AuthorName = Constants.DefaultAgentAuthor,
            AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = type },
        };

    private static ChatMessage CreateDisplayOnlyMessage(string text, string messageId)
    {
        var message = CreateAssistantMessage(text, messageId);
        ConversationHistoryMetadata.ExcludeFromModelHistory(message);
        return message;
    }

    private static Dictionary<string, JsonElement> CreateMetadata(
        AgentRuntimeType? targetType = null,
        Guid? targetId = null,
        string? historyScope = null,
        long? throughSequence = null
    )
    {
        var metadata = new Dictionary<string, JsonElement>();
        if (targetType.HasValue && targetId.HasValue)
        {
            metadata["targetType"] = JsonSerializer.SerializeToElement(
                targetType == AgentRuntimeType.Agent ? "agent" : "agentflow"
            );
            metadata["targetId"] = JsonSerializer.SerializeToElement(targetId.Value.ToString("D"));
        }

        if (historyScope != null)
        {
            metadata["historyScope"] = JsonSerializer.SerializeToElement(historyScope);
        }

        if (throughSequence.HasValue)
        {
            metadata[ConversationHandoffMetadata.ThroughSequenceKey] = JsonSerializer.SerializeToElement(
                throughSequence.Value
            );
        }

        return metadata;
    }

    private static string CreateScope(Guid agentflowId) => $"agentflow:{agentflowId:N}:node:general-agent";
}
