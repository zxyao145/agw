using System.Text.Json;
using Agw.Infrastructure.Data;
using Agw.Projects.Application;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Tests;

/// <summary>
/// 按 Turn 读取对话：倒序分页、按序号排列的 Turn 消息，以及只返回当前用户对话中的数据。
/// Reading a conversation by turn: descending pages, turn messages in sequence order, and data of the current user's conversations only.
/// </summary>
public sealed class ConversationTurnQueryServiceTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path = Path.Combine(
        AppContext.BaseDirectory,
        "test-databases",
        $"agw-turn-query-{Guid.NewGuid():N}.db"
    );
    private readonly TestUserInfoService _user = new("tester");
    private readonly DateTimeOffset _started = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    private readonly Guid _conversationId = Guid.CreateVersion7();
    private readonly Guid _foreignConversationId = Guid.CreateVersion7();
    private readonly List<Guid> _turnIds = [];
    private Guid _foreignTurnId;
    private DbContextOptions<AgwDbContext> _options = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False")
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var context = new AgwDbContext(_options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        Seed(context, "tester", _conversationId, turns: 3);
        _foreignTurnId = Seed(context, "another-user", _foreignConversationId, turns: 1)[0];
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        File.Delete(_path);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ListAsync_Pages_ReturnNewestTurnsFirstWithCursor()
    {
        // Arrange
        await using var context = new AgwDbContext(_options);
        var service = new ConversationTurnQueryService(context, _user);
        var token = TestContext.Current.CancellationToken;

        // Act
        var first = await service.ListAsync(new() { ConversationId = _conversationId, Limit = 2 }, token);
        var second = await service.ListAsync(
            new()
            {
                ConversationId = _conversationId,
                Limit = 2,
                BeforeSequence = first!.NextBeforeSequence,
            },
            token
        );

        // Assert
        Assert.Equal([_turnIds[2], _turnIds[1]], first.Items.Select(item => item.TurnId));
        Assert.True(first.HasMore);
        Assert.Equal(2, first.NextBeforeSequence);
        Assert.Equal([_turnIds[0]], second!.Items.Select(item => item.TurnId));
        Assert.False(second.HasMore);
        Assert.Null(second.NextBeforeSequence);
        var latest = first.Items[0];
        Assert.Equal("completed", latest.Status);
        Assert.Equal("agent", latest.AgentType);
        Assert.Equal("request 3", latest.InputSummary);
        Assert.Equal(4, latest.FirstSequence);
        Assert.Equal(5, latest.LastSequence);
        Assert.Equal(_turnIds[2], latest.InputMessageId);
    }

    [Fact]
    public async Task ListAsync_ForeignConversation_ReturnsNull()
    {
        // Arrange
        await using var context = new AgwDbContext(_options);
        var service = new ConversationTurnQueryService(context, _user);

        // Act
        var page = await service.ListAsync(
            new() { ConversationId = _foreignConversationId },
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Null(page);
    }

    [Fact]
    public async Task GetMessagesAsync_OwnedTurn_ReturnsMessagesInSequenceOrder()
    {
        // Arrange
        await using var context = new AgwDbContext(_options);
        var service = new ConversationTurnQueryService(context, _user);

        // Act
        var messages = await service.GetMessagesAsync(
            new() { ConversationId = _conversationId, TurnId = _turnIds[1] },
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(_turnIds[1], messages!.TurnId);
        Assert.Equal([2L, 3L], messages.Items.Select(item => item.Sequence));
        Assert.Equal([0, 1], messages.Items.Select(item => item.StepIndex));
        Assert.Equal(
            ["request 2", "answer 2"],
            messages.Items.Select(item =>
                string.Concat(item.Message.Contents.OfType<AgwTextContent>().Select(content => content.Content))
            )
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMessagesAsync_TurnOutsideOwnedConversation_ReturnsNull(bool foreignConversation)
    {
        // Arrange
        await using var context = new AgwDbContext(_options);
        var service = new ConversationTurnQueryService(context, _user);

        // Act
        var messages = await service.GetMessagesAsync(
            foreignConversation
                ? new() { ConversationId = _foreignConversationId, TurnId = _foreignTurnId }
                : new() { ConversationId = _conversationId, TurnId = _foreignTurnId },
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Null(messages);
    }

    /// <summary>
    /// 写入一个对话与若干已完成的 Turn：每个 Turn 有一条用户输入与一条 Agent 回答。
    /// Writes a conversation with completed turns, each with one user input and one Agent answer.
    /// </summary>
    private List<Guid> Seed(AgwDbContext context, string owner, Guid conversationId, int turns)
    {
        var projectId = Guid.CreateVersion7();
        context.Projects.Add(
            new Project
            {
                Id = projectId,
                Name = conversationId.ToString("N"),
                CreateBy = owner,
            }
        );
        context.ProjectConversations.Add(
            new ProjectConversation
            {
                Id = conversationId,
                ProjectId = projectId,
                ContextId = conversationId.ToString("N"),
                CreateBy = owner,
            }
        );
        var ids = new List<Guid>();
        for (var index = 0; index < turns; index++)
        {
            var turnId = Guid.CreateVersion7();
            var sequence = index * 2L;
            context.ProjectConversationTurns.Add(
                new ProjectConversationTurn
                {
                    Id = turnId,
                    ProjectConversationId = conversationId,
                    TargetId = Guid.CreateVersion7(),
                    RuntimeType = AgentRuntimeType.Agent,
                    Status = ProjectConversationTurnStatus.Completed,
                    InputMessageId = turnId,
                    FirstSequence = sequence,
                    LastSequence = sequence + 1,
                    StepCount = 1,
                    StartedAt = _started.AddMinutes(index),
                    FinishedAt = _started.AddMinutes(index).AddSeconds(30),
                }
            );
            context.ProjectConversationChatHistories.AddRange(
                CreateRow(
                    turnId,
                    conversationId,
                    turnId,
                    sequence,
                    0,
                    ConversationMessagePurpose.Input,
                    new ChatMessage(ChatRole.User, $"request {index + 1}")
                ),
                CreateRow(
                    Guid.CreateVersion7(),
                    conversationId,
                    turnId,
                    sequence + 1,
                    1,
                    ConversationMessagePurpose.Message,
                    new ChatMessage(ChatRole.Assistant, $"answer {index + 1}")
                )
            );
            ids.Add(turnId);
        }
        if (owner == _user.UserId)
            _turnIds.AddRange(ids);
        return ids;
    }

    private ProjectConversationChatHistory CreateRow(
        Guid id,
        Guid conversationId,
        Guid turnId,
        long sequence,
        int stepIndex,
        ConversationMessagePurpose purpose,
        ChatMessage message
    )
    {
        message.MessageId = id.ToString("D");
        return new ProjectConversationChatHistory
        {
            Id = id,
            ConversationId = conversationId,
            TaskId = turnId,
            Status = TaskExecutionStatus.Succeeded,
            ConversationSequence = sequence,
            ConversationPayload = JsonSerializer.Serialize(message, PayloadJsonOptions),
            TurnId = turnId,
            StepIndex = stepIndex,
            Purpose = purpose,
            CreateTime = _started.AddSeconds(sequence),
        };
    }
}
