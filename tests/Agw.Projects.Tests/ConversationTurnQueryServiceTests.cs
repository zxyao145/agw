using System.Text.Json;
using Agw.Infrastructure.Data;
using Agw.Projects.Application;
using Agw.Projects.Contracts.History;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
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
                BeforeTurnId = first.NextBeforeTurnId,
            },
            token
        );

        // Assert
        Assert.Equal([_turnIds[2], _turnIds[1]], first.Items.Select(item => item.TurnId));
        Assert.True(first.HasMore);
        Assert.Equal(2, first.NextBeforeSequence);
        Assert.Equal(_turnIds[1], first.NextBeforeTurnId);
        Assert.Equal([_turnIds[0]], second!.Items.Select(item => item.TurnId));
        Assert.False(second.HasMore);
        Assert.Null(second.NextBeforeSequence);
        Assert.Null(second.NextBeforeTurnId);
        var latest = first.Items[0];
        Assert.Equal("completed", latest.Status);
        Assert.Equal("agent", latest.AgentType);
        Assert.Equal("request 3", latest.InputSummary);
        Assert.Equal(4, latest.FirstSequence);
        Assert.Equal(5, latest.LastSequence);
        Assert.Equal(_turnIds[2], latest.InputMessageId);
    }

    [Fact]
    public async Task ListAsync_MoreThanOneHundredInputs_ReadsCompleteConversationWithoutMessages()
    {
        // Arrange / 准备超过一页的用户回合。
        var token = TestContext.Current.CancellationToken;
        var conversationId = Guid.CreateVersion7();
        await using var context = new AgwDbContext(_options);
        var turnIds = Seed(context, "tester", conversationId, turns: 205);
        await context.SaveChangesAsync(token);
        var service = new ConversationTurnQueryService(context, _user);
        var inputs = new List<ConversationTurnResponse>();
        ConversationTurnPageResponse? page = null;

        // Act / 沿游标读取全部回合摘要。
        do
        {
            page = await service.ListAsync(
                new()
                {
                    ConversationId = conversationId,
                    Limit = 100,
                    BeforeSequence = page?.NextBeforeSequence,
                    BeforeTurnId = page?.NextBeforeTurnId,
                },
                token
            );
            inputs.AddRange(page!.Items);
        } while (page.HasMore);

        // Assert / 确认顺序、输入身份和预览完整。
        Assert.Equal(turnIds.AsEnumerable().Reverse(), inputs.Select(input => input.InputMessageId));
        Assert.Equal(205, inputs.Select(input => input.InputMessageId).Distinct().Count());
        Assert.All(inputs, input => Assert.StartsWith("request ", input.InputSummary));
    }

    [Fact]
    public async Task ListAsync_ImageAndTextInputs_ReturnsNormalizedPreviews()
    {
        // Arrange / 准备输入消息。
        var token = TestContext.Current.CancellationToken;
        await using var context = new AgwDbContext(_options);
        var rows = await context
            .ProjectConversationChatHistories.Where(row => _turnIds.Contains(row.Id))
            .OrderBy(row => row.ConversationSequence)
            .ToListAsync(token);
        var image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aT1sAAAAASUVORK5CYII="
        );
        ChatMessage[] messages =
        [
            new(
                ChatRole.User,
                [
                    new DataContent(image, "image/png") { Name = " diagram.png " },
                    new DataContent(image, "image/png") { Name = "details.png" },
                ]
            ),
            new(ChatRole.User, [new DataContent(image, "image/png")]),
            new(
                ChatRole.User,
                [new TextContent("  Review\n\n  this  "), new TextContent(string.Concat(Enumerable.Repeat("😀", 210)))]
            ),
        ];
        for (var index = 0; index < rows.Count; index++)
            rows[index].ConversationPayload = JsonSerializer.Serialize(messages[index], PayloadJsonOptions);
        await context.SaveChangesAsync(token);

        // Act / 查询 Turn 摘要。
        var page = await new ConversationTurnQueryService(context, _user).ListAsync(
            new() { ConversationId = _conversationId },
            token
        );

        // Assert / 检查附件名称、空白处理和 Unicode 字符完整性。
        Assert.Equal("diagram.png, details.png", page!.Items[2].InputSummary);
        Assert.Equal("Image input", page.Items[1].InputSummary);
        Assert.Equal("Review this " + string.Concat(Enumerable.Repeat("😀", 188)), page.Items[0].InputSummary);
    }

    [Fact]
    public async Task ListAsync_TurnsSharingFirstSequence_PagesReturnEveryTurnOnce()
    {
        // Arrange: turns without input write no message, so they and the next input turn share first_sequence 0.
        var token = TestContext.Current.CancellationToken;
        var conversationId = Guid.CreateVersion7();
        await using (var seed = new AgwDbContext(_options))
        {
            Seed(seed, "tester", conversationId, turns: 0);
            await seed.SaveChangesAsync(token);
        }
        var accepted = new List<Guid>();
        for (var index = 0; index < 4; index++)
        {
            await using var context = new AgwDbContext(_options);
            var turnId = Guid.CreateVersion7();
            var input = index == 3 ? CreateInput(turnId) : null;
            await new ConversationTurnStore(context, TimeProvider.System).AcceptAsync(
                new AcceptConversationTurnRequest
                {
                    TurnId = turnId,
                    ProjectId = await context
                        .ProjectConversations.Where(conversation => conversation.Id == conversationId)
                        .Select(conversation => conversation.ProjectId)
                        .SingleAsync(token),
                    ContextId = conversationId.ToString("N"),
                    ConversationId = conversationId,
                    Generation = 0,
                    TargetId = Guid.CreateVersion7(),
                    TargetType = ConversationTurnTargetType.Agentflow,
                    Input = input,
                },
                token
            );
            accepted.Add(turnId);
        }
        await using var queryContext = new AgwDbContext(_options);
        var service = new ConversationTurnQueryService(queryContext, _user);

        // Act
        var pages = new List<ConversationTurnPageResponse>();
        ConversationTurnPageResponse? page = null;
        do
        {
            page = await service.ListAsync(
                new()
                {
                    ConversationId = conversationId,
                    Limit = 1,
                    BeforeSequence = page?.NextBeforeSequence,
                    BeforeTurnId = page?.NextBeforeTurnId,
                },
                token
            );
            pages.Add(page!);
        } while (page!.HasMore);

        // Assert
        var items = pages.SelectMany(item => item.Items).ToList();
        Assert.All(items, item => Assert.Equal(0, item.FirstSequence));
        Assert.Equal(accepted.Order(), items.Select(item => item.TurnId).Order());
        Assert.Equal(4, pages.Count);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ListAsync_IncompleteCursor_ThrowsInvalidParam(bool withSequence, bool withTurnId)
    {
        // Arrange
        await using var context = new AgwDbContext(_options);
        var service = new ConversationTurnQueryService(context, _user);

        // Act
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            service.ListAsync(
                new()
                {
                    ConversationId = _conversationId,
                    BeforeSequence = withSequence ? 2 : null,
                    BeforeTurnId = withTurnId ? _turnIds[1] : null,
                },
                TestContext.Current.CancellationToken
            )
        );

        // Assert
        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
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

    [Fact]
    public async Task GetActivityAsync_LatestTurnStatuses_ReturnsOnlyNonIdleConversations()
    {
        // Arrange: one conversation per latest-turn status, plus one without turns.
        // 准备：每种最新 Turn 状态各一个会话，另加一个没有 Turn 的会话。
        var token = TestContext.Current.CancellationToken;
        await using var context = new AgwDbContext(_options);
        var projectId = AddProject(context, "tester");
        var expected = new Dictionary<Guid, (Guid TurnId, string Status)>();
        foreach (
            var (status, name) in new (ProjectConversationTurnStatus Status, string? Name)[]
            {
                (ProjectConversationTurnStatus.Accepted, "running"),
                (ProjectConversationTurnStatus.Running, "running"),
                (ProjectConversationTurnStatus.Failed, "failed"),
                (ProjectConversationTurnStatus.Interrupted, "interrupted"),
                (ProjectConversationTurnStatus.Completed, null),
            }
        )
        {
            var conversationId = AddConversation(context, projectId, "tester");
            AddTurn(context, conversationId, ProjectConversationTurnStatus.Completed, 0);
            var turnId = AddTurn(context, conversationId, status, 2);
            if (name != null)
                expected[conversationId] = (turnId, name);
        }
        AddConversation(context, projectId, "tester");
        await context.SaveChangesAsync(token);

        // Act
        var activity = await new ConversationTurnQueryService(context, _user).GetActivityAsync(
            new() { ProjectId = projectId },
            token
        );

        // Assert
        Assert.Equal(
            expected.OrderBy(item => item.Key),
            activity!
                .Items.Select(item => KeyValuePair.Create(item.ConversationId, (item.TurnId, item.Status)))
                .OrderBy(item => item.Key)
        );
    }

    [Fact]
    public async Task GetActivityAsync_EarlierTurnStillRunning_ReturnsRunningWithThatTurn()
    {
        // Arrange: the latest turn finished while an earlier turn is still running.
        // 准备：最新 Turn 已结束，较早的 Turn 仍在运行。
        var token = TestContext.Current.CancellationToken;
        await using var context = new AgwDbContext(_options);
        var projectId = AddProject(context, "tester");
        var conversationId = AddConversation(context, projectId, "tester");
        var runningTurnId = AddTurn(context, conversationId, ProjectConversationTurnStatus.Running, 0);
        AddTurn(context, conversationId, ProjectConversationTurnStatus.Failed, 2);
        await context.SaveChangesAsync(token);

        // Act
        var activity = await new ConversationTurnQueryService(context, _user).GetActivityAsync(
            new() { ProjectId = projectId },
            token
        );

        // Assert
        var item = Assert.Single(activity!.Items);
        Assert.Equal(conversationId, item.ConversationId);
        Assert.Equal(runningTurnId, item.TurnId);
        Assert.Equal("running", item.Status);
    }

    [Theory]
    [InlineData(ProjectConversationTurnStatus.Failed, ProjectConversationTurnStatus.Interrupted, "interrupted")]
    [InlineData(ProjectConversationTurnStatus.Interrupted, ProjectConversationTurnStatus.Failed, "failed")]
    [InlineData(ProjectConversationTurnStatus.Failed, ProjectConversationTurnStatus.Completed, null)]
    public async Task GetActivityAsync_TurnsSharingFirstSequence_UsesTurnWithLargestId(
        ProjectConversationTurnStatus earlierStatus,
        ProjectConversationTurnStatus laterStatus,
        string? expectedStatus
    )
    {
        // Arrange: a turn without input and the next turn share first_sequence; the larger turn ID is the latest.
        // 准备：没有输入的 Turn 与下一个 Turn 共用 first_sequence，Turn ID 较大的为最新。
        var token = TestContext.Current.CancellationToken;
        await using var context = new AgwDbContext(_options);
        var projectId = AddProject(context, "tester");
        var conversationId = AddConversation(context, projectId, "tester");
        var laterTurnId = AddTurn(
            context,
            conversationId,
            laterStatus,
            3,
            Guid.Parse("01994000-0000-7000-8000-000000000002")
        );
        AddTurn(context, conversationId, earlierStatus, 3, Guid.Parse("01994000-0000-7000-8000-000000000001"));
        await context.SaveChangesAsync(token);

        // Act
        var activity = await new ConversationTurnQueryService(context, _user).GetActivityAsync(
            new() { ProjectId = projectId },
            token
        );

        // Assert
        if (expectedStatus == null)
        {
            Assert.Empty(activity!.Items);
            return;
        }
        var item = Assert.Single(activity!.Items);
        Assert.Equal(laterTurnId, item.TurnId);
        Assert.Equal(expectedStatus, item.Status);
    }

    [Fact]
    public async Task GetActivityAsync_ForeignProject_ReturnsNull()
    {
        // Arrange: another user's project with a running conversation.
        // 准备：其他用户的项目，其中有运行中的会话。
        var token = TestContext.Current.CancellationToken;
        await using var context = new AgwDbContext(_options);
        var projectId = AddProject(context, "another-user");
        var conversationId = AddConversation(context, projectId, "another-user");
        AddTurn(context, conversationId, ProjectConversationTurnStatus.Running, 0);
        await context.SaveChangesAsync(token);
        var service = new ConversationTurnQueryService(context, _user);

        // Act
        var foreign = await service.GetActivityAsync(new() { ProjectId = projectId }, token);
        var missing = await service.GetActivityAsync(new() { ProjectId = Guid.CreateVersion7() }, token);

        // Assert
        Assert.Null(foreign);
        Assert.Null(missing);
    }

    private static Guid AddProject(AgwDbContext context, string owner)
    {
        var projectId = Guid.CreateVersion7();
        context.Projects.Add(
            new Project
            {
                Id = projectId,
                Name = projectId.ToString("N"),
                CreateBy = owner,
            }
        );
        return projectId;
    }

    private static Guid AddConversation(AgwDbContext context, Guid projectId, string owner)
    {
        var conversationId = Guid.CreateVersion7();
        context.ProjectConversations.Add(
            new ProjectConversation
            {
                Id = conversationId,
                ProjectId = projectId,
                ContextId = conversationId.ToString("N"),
                CreateBy = owner,
            }
        );
        return conversationId;
    }

    private Guid AddTurn(
        AgwDbContext context,
        Guid conversationId,
        ProjectConversationTurnStatus status,
        long firstSequence,
        Guid? id = null
    )
    {
        var turnId = id ?? Guid.CreateVersion7();
        context.ProjectConversationTurns.Add(
            new ProjectConversationTurn
            {
                Id = turnId,
                ProjectConversationId = conversationId,
                TargetId = Guid.CreateVersion7(),
                RuntimeType = AgentRuntimeType.Agent,
                Status = status,
                FirstSequence = firstSequence,
                StartedAt = _started,
            }
        );
        return turnId;
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

    private ConversationTurnInput CreateInput(Guid turnId)
    {
        var messageId = Guid.CreateVersion7();
        return new ConversationTurnInput(
            messageId,
            _started,
            "user",
            JsonSerializer.Serialize(
                new ChatMessage(ChatRole.User, $"request for {turnId:N}") { MessageId = messageId.ToString("D") },
                PayloadJsonOptions
            )
        );
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
