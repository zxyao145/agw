using System.Security.Claims;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Durable;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Shared;
using Agw.Shared.Data.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed class DurableExecutionAuditTests
{
    [Fact]
    public async Task StateTransition_UsesPersistedExecutionOwnerForAudit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var auditProvider = new CurrentUserAuditProvider();
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                new EntityCreatorInterceptor(auditProvider, TimeProvider.System),
                new EntityModifierInterceptor(auditProvider, TimeProvider.System),
                new EntitySoftDeleteInterceptor(auditProvider, TimeProvider.System)
            )
            .Options;
        await using var context = new AgwDbContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var store = new DurableExecutionStore(context, TimeProvider.System);
        var executionId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();

        using (UserInfoUtil.Push(CreatePrincipal("owner")))
        {
            await store.RegisterAsync(
                executionId,
                "owner",
                Guid.CreateVersion7(),
                AgentRuntimeType.Agent,
                new AgwUserInput { MessageId = "message-1", Contents = [new AgwTextContent { Content = "run" }] },
                new AgentExecutionTask
                {
                    TaskId = Guid.CreateVersion7(),
                    ProjectConversationId = Guid.CreateVersion7(),
                    ProjectId = projectId,
                    ContextId = "context-1",
                    Title = "Audit test",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                },
                ExecutionSettings.FromCommand(new SettingCommand(projectId, contextId: "context-1")),
                cancellationToken
            );
        }

        using (UserInfoUtil.PushSystemScope())
        {
            var running = await store.TryBeginSegmentAsync(
                executionId,
                TimeProvider.System.GetUtcNow(),
                cancellationToken
            );
            Assert.NotNull(running);
        }

        context.ChangeTracker.Clear();
        using (UserInfoUtil.PushSystemScope())
        {
            var persisted = await context.DurableExecutions.SingleAsync(cancellationToken);
            Assert.Equal("owner", persisted.CreateBy);
            Assert.Equal("owner", persisted.UpdateBy);
        }
    }

    private static ClaimsPrincipal CreatePrincipal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private sealed class CurrentUserAuditProvider : IEntityAuditUserIdProvider
    {
        public string GetUserId() => UserInfoUtil.UserId ?? Constants.AdminUserId;
    }
}
