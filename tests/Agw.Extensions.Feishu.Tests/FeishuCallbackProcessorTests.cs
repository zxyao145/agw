using System.Text.Json;
using Agw.Extensions.Sdk.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Agw.Extensions.Feishu.Tests;

public sealed class FeishuCallbackProcessorTests
{
    [Fact]
    public async Task ProcessAsync_OfficialDispatcherPayload_WritesOneCallbackAndSubmitAnswer()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid extensionId = FeishuExtension.ExtensionId;
        Guid appId = Guid.CreateVersion7();
        Guid targetId = Guid.CreateVersion7();
        Guid notificationId = Guid.CreateVersion7();
        var identity = new ExtensionDatabaseIdentity(extensionId, Guid.CreateVersion7(), 1);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        DbContextOptions<FeishuDbContext> options = new DbContextOptionsBuilder<FeishuDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new FeishuDbContext(options, identity, new TestUserContext("alice"), TimeProvider.System);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        var app = new FeishuApp
        {
            Id = appId,
            Name = "App",
            Mode = FeishuAppMode.App,
            AppId = "cli_test",
        };
        db.Apps.Add(app);
        await db.SaveChangesAsync(cancellationToken);
        db.Targets.Add(
            new FeishuTarget
            {
                Id = targetId,
                RootId = appId,
                Kind = FeishuTargetKind.Chat,
                ReceiveId = "oc_test",
                Name = "Chat",
            }
        );
        db.Messages.Add(
            new FeishuMessage
            {
                RootId = appId,
                DispatchKey = "dispatch-1",
                NotificationId = notificationId,
                TargetId = targetId,
                MessageId = "om_test",
            }
        );
        db.IdentityBindings.Add(
            new FeishuIdentityBinding
            {
                RootId = appId,
                OpenId = "ou_test",
                UserId = "alice",
            }
        );
        await db.SaveChangesAsync(cancellationToken);

        JsonElement payload = JsonSerializer.SerializeToElement(
            new
            {
                event_type = "card.action.trigger",
                event_id = "evt-1",
                app_id = "cli_test",
                @operator = new { open_id = "ou_test" },
                context = new { open_message_id = "om_test" },
                action = new
                {
                    value = new
                    {
                        notificationId = notificationId.ToString("D"),
                        dispatchKey = "dispatch-1",
                        option = 1,
                    },
                },
            }
        );

        FeishuCallbackAcknowledgement acknowledgement = await FeishuCallbackProcessor.ProcessAsync(
            db,
            app,
            extensionId,
            FeishuApiJsonContext.Default.Options,
            payload,
            cancellationToken
        );
        FeishuCallbackAcknowledgement duplicate = await FeishuCallbackProcessor.ProcessAsync(
            db,
            app,
            extensionId,
            FeishuApiJsonContext.Default.Options,
            payload,
            cancellationToken
        );

        Assert.Equal(FeishuCallbackState.Processing, acknowledgement.State);
        Assert.Equal("Received. Processing the answer.", acknowledgement.Message);
        Assert.Equal(FeishuCallbackState.Processing, duplicate.State);
        FeishuCallback callback = await db.Callbacks.SingleAsync(row => row.EventId == "evt-1", cancellationToken);
        Assert.Equal(appId, callback.RootId);
        Assert.Equal("alice", callback.UserId);
        Assert.Equal(targetId, callback.TargetId);
        Assert.Equal(notificationId, callback.NotificationId);
        Assert.Equal(1, callback.Option);
        Assert.NotNull(callback.OutboxId);
        var outbox = Assert.Single(
            db.ChangeTracker.Entries(),
            entry => entry.Metadata.ClrType.Name == "ExtensionOutboxRow"
        );
        Assert.Equal(EntityState.Unchanged, outbox.State);
        Assert.Equal($"feishu:{appId:D}:evt-1", outbox.Property("IdempotencyKey").CurrentValue);
        Assert.Equal("SubmitAnswer", outbox.Property("Kind").CurrentValue);
    }

    [Fact]
    public async Task ProcessAsync_BindingBelongsToAnotherUser_RejectsWithoutSubmittingAnswer()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid extensionId = FeishuExtension.ExtensionId;
        Guid appId = Guid.CreateVersion7();
        Guid targetId = Guid.CreateVersion7();
        Guid notificationId = Guid.CreateVersion7();
        var identity = new ExtensionDatabaseIdentity(extensionId, Guid.CreateVersion7(), 1);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        DbContextOptions<FeishuDbContext> options = new DbContextOptionsBuilder<FeishuDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new FeishuDbContext(options, identity, new TestUserContext("alice"), TimeProvider.System);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        var app = new FeishuApp
        {
            Id = appId,
            Name = "App",
            Mode = FeishuAppMode.App,
            AppId = "cli_test",
        };
        db.Apps.Add(app);
        await db.SaveChangesAsync(cancellationToken);
        db.Targets.Add(
            new FeishuTarget
            {
                Id = targetId,
                RootId = appId,
                Kind = FeishuTargetKind.Chat,
                ReceiveId = "oc_test",
                Name = "Chat",
            }
        );
        db.Messages.Add(
            new FeishuMessage
            {
                RootId = appId,
                DispatchKey = "dispatch-1",
                NotificationId = notificationId,
                TargetId = targetId,
                MessageId = "om_test",
            }
        );
        db.IdentityBindings.Add(
            new FeishuIdentityBinding
            {
                RootId = appId,
                OpenId = "ou_test",
                UserId = "bob",
            }
        );
        await db.SaveChangesAsync(cancellationToken);

        JsonElement payload = JsonSerializer.SerializeToElement(
            new
            {
                event_type = "card.action.trigger",
                event_id = "evt-foreign-binding",
                app_id = "cli_test",
                @operator = new { open_id = "ou_test" },
                context = new { open_message_id = "om_test" },
                action = new
                {
                    value = new
                    {
                        notificationId = notificationId.ToString("D"),
                        dispatchKey = "dispatch-1",
                        option = 1,
                    },
                },
            }
        );

        FeishuCallbackAcknowledgement acknowledgement = await FeishuCallbackProcessor.ProcessAsync(
            db,
            app,
            extensionId,
            FeishuApiJsonContext.Default.Options,
            payload,
            cancellationToken
        );

        FeishuCallback callback = await db.Callbacks.SingleAsync(
            row => row.EventId == "evt-foreign-binding",
            cancellationToken
        );
        Assert.Equal(FeishuCallbackState.Rejected, acknowledgement.State);
        Assert.Equal(FeishuCallbackState.Rejected, callback.State);
        Assert.Null(callback.UserId);
        Assert.Null(callback.OutboxId);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.Metadata.ClrType.Name == "ExtensionOutboxRow");
    }

    private sealed class TestUserContext(string userId) : IExtensionUserContext
    {
        public string? UserId => userId;

        public Guid? InvocationId => Guid.CreateVersion7();
    }
}
