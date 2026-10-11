using System.Data.Common;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agw.Projects.Tests;

public sealed class TaskSessionBindingServiceTests : IDisposable
{
    private readonly IDisposable _user = ProviderSessionTestFixture.PushUser(ProviderSessionTestFixture.Owner);

    public void Dispose() => _user.Dispose();

    [Fact]
    public async Task UpsertAsync_NewSession_ArchivesPreviousAndPreservesHistory()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var first = await fixture.SaveAsync("11111111-1111-1111-1111-111111111111");
        var archiveTime = ProviderSessionTestFixture.StartTime.AddHours(1);
        fixture.Time.SetUtcNow(archiveTime);

        var second = await fixture.SaveAsync(" 22222222-2222-2222-2222-222222222222 ");

        var bindings = await fixture.ReadBindingsAsync();
        Assert.Equal(2, bindings.Count);
        var archived = Assert.Single(bindings, binding => !binding.IsActive);
        Assert.Equal(first.Id, archived.Id);
        Assert.Equal("11111111-1111-1111-1111-111111111111", archived.ProviderSessionId);
        Assert.Equal(ProviderSessionTestFixture.Owner, archived.UpdateBy);
        Assert.Equal(archiveTime, archived.UpdateTime);
        var active = Assert.Single(bindings, binding => binding.IsActive);
        Assert.Equal(second.Id, active.Id);
        Assert.Equal("22222222-2222-2222-2222-222222222222", active.ProviderSessionId);
        Assert.Equal(ProviderSessionTestFixture.Owner, active.CreateBy);
        Assert.Equal(archiveTime, active.CreateTime);
        Assert.Null(active.UpdateTime);
        Assert.All(bindings, binding => Assert.Equal(fixture.ConversationId, binding.ProjectConversationId));
        Assert.All(bindings, binding => Assert.Equal("codex", binding.ExternalAgentName));
    }

    [Fact]
    public async Task UpsertAsync_CurrentSession_IsIdempotent()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var first = await fixture.SaveAsync("session-a");
        fixture.Time.SetUtcNow(ProviderSessionTestFixture.StartTime.AddHours(1));

        var second = await fixture.SaveAsync("session-a");

        Assert.Equal(first.Id, second.Id);
        var binding = Assert.Single(await fixture.ReadBindingsAsync());
        Assert.True(binding.IsActive);
        Assert.Equal(ProviderSessionTestFixture.StartTime, binding.CreateTime);
        Assert.Null(binding.UpdateBy);
        Assert.Null(binding.UpdateTime);
    }

    [Fact]
    public async Task UpsertAsync_ArchivedSession_ReturnsConflictAndKeepsArchived()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        await fixture.SaveAsync("session-a");
        await fixture.SaveAsync("session-b");

        var error = await Assert.ThrowsAsync<AgwException>(() => fixture.SaveAsync("session-a"));

        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, error.Code);
        var bindings = await fixture.ReadBindingsAsync();
        Assert.Equal(2, bindings.Count);
        Assert.False(Assert.Single(bindings, binding => binding.ProviderSessionId == "session-a").IsActive);
        Assert.True(Assert.Single(bindings, binding => binding.ProviderSessionId == "session-b").IsActive);
    }

    [Fact]
    public async Task GetAsync_ActiveBinding_ReturnsOnlyActiveRecordForNormalizedIdentity()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        await fixture.SaveAsync("session-a");
        await fixture.SaveAsync("session-b");

        var binding = await fixture.Service.GetAsync(
            fixture.ProjectId,
            ProviderSessionTestFixture.ContextId,
            fixture.AgentId,
            " CODEX ",
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(binding);
        Assert.Equal("session-b", binding.ProviderSessionId);
        Assert.True(binding.IsActive);
    }

    [Fact]
    public async Task GetAsync_GroupWithoutActiveRecord_ReturnsNull()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var binding = await fixture.SaveAsync("session-a");
        await fixture.Service.ArchiveAsync(
            fixture.ProjectId,
            fixture.ConversationId,
            binding.Id,
            TestContext.Current.CancellationToken
        );

        Assert.Null(
            await fixture.Service.GetAsync(
                fixture.ProjectId,
                ProviderSessionTestFixture.ContextId,
                fixture.AgentId,
                "codex",
                TestContext.Current.CancellationToken
            )
        );
        Assert.False(Assert.Single(await fixture.ReadBindingsAsync()).IsActive);
    }

    [Fact]
    public async Task UpsertAsync_AfterArchive_CreatesNewActiveSession()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var first = await fixture.SaveAsync("session-a");
        var archiveTime = ProviderSessionTestFixture.StartTime.AddMinutes(10);
        fixture.Time.SetUtcNow(archiveTime);
        await fixture.Service.ArchiveAsync(
            fixture.ProjectId,
            fixture.ConversationId,
            first.Id,
            TestContext.Current.CancellationToken
        );
        fixture.Time.SetUtcNow(archiveTime.AddMinutes(10));

        await fixture.SaveAsync("session-b");

        var bindings = await fixture.ReadBindingsAsync();
        var archived = Assert.Single(bindings, binding => binding.Id == first.Id);
        Assert.False(archived.IsActive);
        Assert.Equal(archiveTime, archived.UpdateTime);
        Assert.Equal("session-b", Assert.Single(bindings, binding => binding.IsActive).ProviderSessionId);
    }

    [Fact]
    public async Task UpsertAsync_SecondSaveCancelled_RollsBackAndNextSaveStartsClean()
    {
        var interceptor = new CancelBindingInsertInterceptor();
        await using var fixture = await ProviderSessionTestFixture.CreateAsync(interceptor);
        var first = await fixture.SaveAsync("session-a");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        interceptor.Arm(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.SaveAsync("session-b", cancellationToken: cancellation.Token)
        );

        // 第一次保存已经停用原记录，第二次保存被取消后整个事务撤销，原 session 仍然生效。
        // The first save already archived the previous record; cancelling the second save rolls the whole transaction back, so the previous session stays active.
        Assert.Equal(1, interceptor.CancelledInserts);
        var afterFailure = Assert.Single(await fixture.ReadBindingsAsync());
        Assert.Equal(first.Id, afterFailure.Id);
        Assert.True(afterFailure.IsActive);
        Assert.Null(afterFailure.UpdateTime);

        var second = await fixture.SaveAsync("session-b");

        var bindings = await fixture.ReadBindingsAsync();
        Assert.Equal(2, bindings.Count);
        Assert.False(Assert.Single(bindings, binding => binding.Id == first.Id).IsActive);
        Assert.Equal(second.Id, Assert.Single(bindings, binding => binding.IsActive).Id);
    }

    [Fact]
    public async Task UpsertAsync_StaleGeneration_IsRejected()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        await fixture.Context.ProjectConversations.ExecuteUpdateAsync(
            setters => setters.SetProperty(conversation => conversation.Generation, 1),
            TestContext.Current.CancellationToken
        );

        var error = await Assert.ThrowsAsync<AgwException>(() => fixture.SaveAsync("session-a"));

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, error.Code);
        Assert.Empty(await fixture.ReadBindingsAsync());
    }

    [Fact]
    public async Task UpsertAsync_OtherUserOrMissingUser_IsRejected()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        using (ProviderSessionTestFixture.PushUser("other-user"))
        {
            var otherService = fixture.CreateService(fixture.Context, new TestUserInfoService("other-user"));
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                otherService.UpsertAsync(
                    fixture.ProjectId,
                    ProviderSessionTestFixture.ContextId,
                    fixture.AgentId,
                    "codex",
                    "session-a",
                    "other-user",
                    token
                )
            );
            Assert.Equal(ErrorCodes.ResourceNotFound.Code, error.Code);
        }

        var anonymousService = fixture.CreateService(fixture.Context, new TestUserInfoService(""));
        var anonymousError = await Assert.ThrowsAsync<AgwException>(() =>
            anonymousService.UpsertAsync(
                fixture.ProjectId,
                ProviderSessionTestFixture.ContextId,
                fixture.AgentId,
                "codex",
                "session-a",
                ProviderSessionTestFixture.Owner,
                token
            )
        );
        Assert.Equal(ErrorCodes.AuthenticationRequired.Code, anonymousError.Code);
        Assert.Empty(await fixture.ReadBindingsAsync());
    }

    [Fact]
    public async Task UpsertAsync_WhenSessionsSavedConcurrently_KeepsSingleActiveBinding()
    {
        var token = TestContext.Current.CancellationToken;
        using var user = ProviderSessionTestFixture.PushUser(ProviderSessionTestFixture.Owner);
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-data"));
        var dbPath = Path.Combine(directory.FullName, $"agw-binding-{Guid.CreateVersion7():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=False")
                .UseSnakeCaseNamingConvention()
                .Options;
            await using (var setupContext = new AgwDbContext(options))
            {
                await setupContext.Database.EnsureCreatedAsync(token);
                setupContext.Projects.Add(
                    new Project
                    {
                        Id = Guid.CreateVersion7(),
                        Name = "Binding Project",
                        Type = ProjectType.UserDefined,
                        CreateBy = ProviderSessionTestFixture.Owner,
                        CreateTime = ProviderSessionTestFixture.StartTime,
                    }
                );
                await setupContext.SaveChangesAsync(token);
            }

            Guid projectId;
            var conversationId = Guid.CreateVersion7();
            var agentId = Guid.CreateVersion7();
            await using (var seedContext = new AgwDbContext(options))
            {
                projectId = (await seedContext.Projects.SingleAsync(token)).Id;
                seedContext.ProjectConversations.Add(
                    new ProjectConversation
                    {
                        Id = conversationId,
                        ProjectId = projectId,
                        ContextId = ProviderSessionTestFixture.ContextId,
                        Title = "Binding Context",
                        CreateBy = ProviderSessionTestFixture.Owner,
                        CreateTime = ProviderSessionTestFixture.StartTime,
                    }
                );
                await seedContext.SaveChangesAsync(token);
            }

            await using var services = TestProjectPersistence.CreateProviderSessionServices(options);
            var sessionIds = Enumerable.Range(0, 12).Select(_ => Guid.CreateVersion7().ToString("D")).ToArray();
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var upserts = sessionIds
                .Select(async sessionId =>
                {
                    await startGate.Task.WaitAsync(token);
                    await using var dbContext = new AgwDbContext(options);
                    var service = TestProjectPersistence.CreateBindingService(
                        dbContext,
                        services,
                        new TestAgentCatalogFacade(
                            new Agw.Infrastructure.Repositories.EfRepository<Agw.Shared.Data.Entities.Agents.McpServer>(
                                dbContext
                            )
                        )
                    );
                    return await service.UpsertAsync(
                        projectId,
                        ProviderSessionTestFixture.ContextId,
                        agentId,
                        "codex",
                        sessionId,
                        ProviderSessionTestFixture.Owner,
                        token
                    );
                })
                .ToArray();

            startGate.SetResult();
            await Task.WhenAll(upserts);

            await using var verifyContext = new AgwDbContext(options);
            var bindings = await verifyContext
                .ProjectConversationBindings.AsNoTracking()
                .Where(binding => binding.ProjectConversationId == conversationId)
                .ToListAsync(token);
            Assert.Equal(sessionIds.Order(), bindings.Select(binding => binding.ProviderSessionId).Order());
            var active = Assert.Single(bindings, binding => binding.IsActive);
            Assert.Contains(active.Id, upserts.Select(task => task.Result.Id));
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ListAsync_Conversation_ReturnsEveryGroupInDescendingOrder()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var otherAgentId = Guid.CreateVersion7();
        await fixture.SaveAsync("session-a");
        fixture.Time.SetUtcNow(ProviderSessionTestFixture.StartTime.AddMinutes(1));
        await fixture.SaveAsync("session-b");
        fixture.Time.SetUtcNow(ProviderSessionTestFixture.StartTime.AddMinutes(2));
        await fixture.SaveAsync("session-a", otherAgentId, "claude-code");

        var bindings = await fixture.Service.ListAsync(
            fixture.ProjectId,
            fixture.ConversationId,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["session-a", "session-b", "session-a"], bindings.Select(binding => binding.ProviderSessionId));
        Assert.Equal([otherAgentId, fixture.AgentId, fixture.AgentId], bindings.Select(binding => binding.AgentId));
        Assert.Equal([true, true, false], bindings.Select(binding => binding.IsActive));
    }

    [Fact]
    public async Task ListAsync_ForeignOrMissingConversation_ReturnsNotFound()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await fixture.SaveAsync("session-a");
        var otherProjectId = Guid.CreateVersion7();
        var otherConversationId = Guid.CreateVersion7();
        await fixture.SeedProjectAsync(otherProjectId, "other-user");
        await fixture.SeedConversationAsync(otherProjectId, otherConversationId, "context-2", "other-user");

        var foreign = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.ListAsync(otherProjectId, otherConversationId, token)
        );
        var mismatched = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.ListAsync(otherProjectId, fixture.ConversationId, token)
        );
        var invalid = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.ListAsync(fixture.ProjectId, Guid.Empty, token)
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, foreign.Code);
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, mismatched.Code);
        Assert.Equal(ErrorCodes.InvalidParam.Code, invalid.Code);
    }

    [Fact]
    public async Task ArchiveAsync_ActiveBinding_ArchivesOnlyTarget()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var otherAgentId = Guid.CreateVersion7();
        var target = await fixture.SaveAsync("session-a");
        var sameNameOtherAgent = await fixture.SaveAsync("session-a", otherAgentId);
        var otherName = await fixture.SaveAsync("session-c", externalAgentName: "pi");
        var archiveTime = ProviderSessionTestFixture.StartTime.AddHours(2);
        fixture.Time.SetUtcNow(archiveTime);

        await fixture.Service.ArchiveAsync(
            fixture.ProjectId,
            fixture.ConversationId,
            target.Id,
            TestContext.Current.CancellationToken
        );

        var bindings = await fixture.ReadBindingsAsync();
        var archived = Assert.Single(bindings, binding => binding.Id == target.Id);
        Assert.False(archived.IsActive);
        Assert.Equal(ProviderSessionTestFixture.Owner, archived.UpdateBy);
        Assert.Equal(archiveTime, archived.UpdateTime);
        foreach (var untouched in new[] { sameNameOtherAgent.Id, otherName.Id })
        {
            var binding = Assert.Single(bindings, binding => binding.Id == untouched);
            Assert.True(binding.IsActive);
            Assert.Null(binding.UpdateTime);
        }
    }

    [Fact]
    public async Task ArchiveAsync_ArchivedBinding_IsIdempotent()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var target = await fixture.SaveAsync("session-a");
        var archiveTime = ProviderSessionTestFixture.StartTime.AddHours(1);
        fixture.Time.SetUtcNow(archiveTime);
        await fixture.Service.ArchiveAsync(fixture.ProjectId, fixture.ConversationId, target.Id, token);
        fixture.Time.SetUtcNow(archiveTime.AddHours(1));

        // 已经归档的记录不再经过执行锁，正在执行的对话也可以重复归档。
        // An archived record no longer goes through the execution lock, so a running conversation can archive it again.
        var gate = new ConversationExecutionGate(fixture.Context, InMemoryApplicationLock.Shared, fixture.Time);
        await using (await gate.AcquireAsync(fixture.ConversationId, 0, token))
        {
            await fixture.Service.ArchiveAsync(fixture.ProjectId, fixture.ConversationId, target.Id, token);
        }

        var binding = Assert.Single(await fixture.ReadBindingsAsync());
        Assert.False(binding.IsActive);
        Assert.Equal(archiveTime, binding.UpdateTime);
    }

    [Fact]
    public async Task ArchiveAsync_StaleRecordAfterNewSession_KeepsNewSessionActive()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var stale = await fixture.SaveAsync("session-a");
        var current = await fixture.SaveAsync("session-b");

        await fixture.Service.ArchiveAsync(
            fixture.ProjectId,
            fixture.ConversationId,
            stale.Id,
            TestContext.Current.CancellationToken
        );

        var bindings = await fixture.ReadBindingsAsync();
        Assert.Equal(current.Id, Assert.Single(bindings, binding => binding.IsActive).Id);
    }

    [Fact]
    public async Task ArchiveAsync_SameProviderSessionIdInDifferentGroups_ArchivesSpecifiedRecord()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var codex = await fixture.SaveAsync("shared-session");
        var pi = await fixture.SaveAsync("shared-session", externalAgentName: "pi");

        await fixture.Service.ArchiveAsync(
            fixture.ProjectId,
            fixture.ConversationId,
            pi.Id,
            TestContext.Current.CancellationToken
        );

        var bindings = await fixture.ReadBindingsAsync();
        Assert.True(Assert.Single(bindings, binding => binding.Id == codex.Id).IsActive);
        Assert.False(Assert.Single(bindings, binding => binding.Id == pi.Id).IsActive);
    }

    [Fact]
    public async Task ArchiveAsync_BindingOutsideConversation_ReturnsNotFound()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var otherConversationId = Guid.CreateVersion7();
        await fixture.SeedConversationAsync(
            fixture.ProjectId,
            otherConversationId,
            "context-2",
            ProviderSessionTestFixture.Owner
        );
        var otherBinding = await fixture.Service.UpsertAsync(
            fixture.ProjectId,
            "context-2",
            fixture.AgentId,
            "codex",
            "session-z",
            ProviderSessionTestFixture.Owner,
            token
        );

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.ArchiveAsync(fixture.ProjectId, fixture.ConversationId, otherBinding.Id, token)
        );
        var missing = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.ArchiveAsync(fixture.ProjectId, fixture.ConversationId, Guid.CreateVersion7(), token)
        );
        var invalid = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.ArchiveAsync(fixture.ProjectId, fixture.ConversationId, Guid.Empty, token)
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, error.Code);
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, missing.Code);
        Assert.Equal(ErrorCodes.InvalidParam.Code, invalid.Code);
        Assert.True(Assert.Single(await fixture.ReadBindingsAsync(otherConversationId)).IsActive);
    }

    [Fact]
    public async Task ArchiveAsync_OtherUserOrMissingUser_IsRejected()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var target = await fixture.SaveAsync("session-a");
        using (ProviderSessionTestFixture.PushUser("other-user"))
        {
            var otherService = fixture.CreateService(fixture.Context, new TestUserInfoService("other-user"));
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                otherService.ArchiveAsync(fixture.ProjectId, fixture.ConversationId, target.Id, token)
            );
            Assert.Equal(ErrorCodes.ResourceNotFound.Code, error.Code);
        }

        var anonymousService = fixture.CreateService(fixture.Context, new TestUserInfoService(""));
        var anonymousError = await Assert.ThrowsAsync<AgwException>(() =>
            anonymousService.ArchiveAsync(fixture.ProjectId, fixture.ConversationId, target.Id, token)
        );

        Assert.Equal(ErrorCodes.AuthenticationRequired.Code, anonymousError.Code);
        Assert.True(Assert.Single(await fixture.ReadBindingsAsync()).IsActive);
    }

    [Fact]
    public async Task ArchiveAsync_ActiveInProcessExecution_ReturnsConflictUntilReleased()
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var target = await fixture.SaveAsync("session-a");
        var gate = new ConversationExecutionGate(fixture.Context, InMemoryApplicationLock.Shared, fixture.Time);

        await using (await gate.AcquireAsync(fixture.ConversationId, 0, token))
        {
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                fixture.Service.ArchiveAsync(fixture.ProjectId, fixture.ConversationId, target.Id, token)
            );
            Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, error.Code);
            Assert.True(Assert.Single(await fixture.ReadBindingsAsync()).IsActive);
        }

        await fixture.Service.ArchiveAsync(fixture.ProjectId, fixture.ConversationId, target.Id, token);
        Assert.False(Assert.Single(await fixture.ReadBindingsAsync()).IsActive);
    }

    [Theory]
    [InlineData(DurableExecutionStatus.Queued)]
    [InlineData(DurableExecutionStatus.Running)]
    [InlineData(DurableExecutionStatus.WaitingForHuman)]
    public async Task ArchiveAsync_ActiveDurableExecution_ReturnsConflict(DurableExecutionStatus status)
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var target = await fixture.SaveAsync("session-a");
        await fixture.SeedDurableExecutionAsync(status);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.ArchiveAsync(fixture.ProjectId, fixture.ConversationId, target.Id, token)
        );

        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, error.Code);
        var binding = Assert.Single(await fixture.ReadBindingsAsync());
        Assert.True(binding.IsActive);
        Assert.Null(binding.UpdateTime);
        await using var context = new AgwDbContext(fixture.Options);
        Assert.Equal(status, (await context.DurableExecutions.AsNoTracking().SingleAsync(token)).Status);
    }

    [Theory]
    [InlineData(DurableExecutionStatus.Completed)]
    [InlineData(DurableExecutionStatus.Failed)]
    [InlineData(DurableExecutionStatus.Interrupted)]
    public async Task ArchiveAsync_TerminalDurableExecution_Archives(DurableExecutionStatus status)
    {
        await using var fixture = await ProviderSessionTestFixture.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var target = await fixture.SaveAsync("session-a");
        await fixture.SeedDurableExecutionAsync(status);

        await fixture.Service.ArchiveAsync(fixture.ProjectId, fixture.ConversationId, target.Id, token);

        Assert.False(Assert.Single(await fixture.ReadBindingsAsync()).IsActive);
    }

    /// <summary>
    /// 在协调器保存新绑定记录的 INSERT 之前取消调用方令牌，使保存以真实的取消结束。
    /// Cancels the caller's token right before the coordinator inserts the new binding record, so the save ends in a real cancellation.
    /// </summary>
    private sealed class CancelBindingInsertInterceptor : DbCommandInterceptor
    {
        private CancellationTokenSource? _armed;

        public int CancelledInserts { get; private set; }

        public void Arm(CancellationTokenSource cancellation) => _armed = cancellation;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            CancelInsert(command, cancellationToken);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            CancelInsert(command, cancellationToken);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void CancelInsert(DbCommand command, CancellationToken cancellationToken)
        {
            if (
                _armed is not { } armed
                || !command.CommandText.Contains(
                    "INSERT INTO \"project_conversation_binding\"",
                    StringComparison.Ordinal
                )
            )
            {
                return;
            }

            _armed = null;
            CancelledInserts++;
            armed.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
