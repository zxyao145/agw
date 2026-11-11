using Xunit;

namespace Agw.Extensions.Feishu.Tests;

public sealed class FeishuCallbackRulesTests
{
    [Fact]
    public void MatchesMessage_AllApplicationMessageAndTargetFieldsMatch_ReturnsTrue()
    {
        Guid appId = Guid.CreateVersion7();
        Guid notificationId = Guid.CreateVersion7();
        Guid targetId = Guid.CreateVersion7();
        var app = new FeishuApp
        {
            Id = appId,
            Name = "App",
            AppId = "cli_test",
        };
        var message = new FeishuMessage
        {
            RootId = appId,
            DispatchKey = "dispatch-1",
            NotificationId = notificationId,
            TargetId = targetId,
            MessageId = "message-1",
        };
        var target = new FeishuTarget
        {
            Id = targetId,
            RootId = appId,
            ReceiveId = "chat-1",
            Name = "Chat",
        };

        Assert.True(
            FeishuCallbackRules.MatchesMessage(
                app,
                "cli_test",
                "message-1",
                notificationId,
                "dispatch-1",
                message,
                target
            )
        );
    }

    [Fact]
    public void MatchesMessage_ForeignApplicationTargetIsRejected()
    {
        Guid appId = Guid.CreateVersion7();
        Guid notificationId = Guid.CreateVersion7();
        Guid targetId = Guid.CreateVersion7();
        var app = new FeishuApp
        {
            Id = appId,
            Name = "App",
            AppId = "cli_test",
        };
        var message = new FeishuMessage
        {
            RootId = appId,
            DispatchKey = "dispatch-1",
            NotificationId = notificationId,
            TargetId = targetId,
            MessageId = "message-1",
        };
        var target = new FeishuTarget
        {
            Id = targetId,
            RootId = Guid.CreateVersion7(),
            ReceiveId = "chat-1",
            Name = "Chat",
        };

        Assert.False(
            FeishuCallbackRules.MatchesMessage(
                app,
                "cli_test",
                "message-1",
                notificationId,
                "dispatch-1",
                message,
                target
            )
        );
    }

    [Fact]
    public void MatchesMessage_MismatchedMessageNotificationOrDispatchKeyIsRejected()
    {
        Guid appId = Guid.CreateVersion7();
        Guid notificationId = Guid.CreateVersion7();
        Guid targetId = Guid.CreateVersion7();
        var app = new FeishuApp
        {
            Id = appId,
            Name = "App",
            AppId = "cli_test",
        };
        var message = new FeishuMessage
        {
            RootId = appId,
            DispatchKey = "dispatch-1",
            NotificationId = notificationId,
            TargetId = targetId,
            MessageId = "message-1",
        };
        var target = new FeishuTarget
        {
            Id = targetId,
            RootId = appId,
            ReceiveId = "chat-1",
            Name = "Chat",
        };

        Assert.False(
            FeishuCallbackRules.MatchesMessage(app, "other", "message-1", notificationId, "dispatch-1", message, target)
        );
        Assert.False(
            FeishuCallbackRules.MatchesMessage(app, "cli_test", "other", notificationId, "dispatch-1", message, target)
        );
        Assert.False(
            FeishuCallbackRules.MatchesMessage(
                app,
                "cli_test",
                "message-1",
                Guid.CreateVersion7(),
                "dispatch-1",
                message,
                target
            )
        );
        Assert.False(
            FeishuCallbackRules.MatchesMessage(app, "cli_test", "message-1", notificationId, "other", message, target)
        );
    }

    [Fact]
    public void MatchesIdentity_ForeignOpenIdAppOrUserIsRejected()
    {
        Guid appId = Guid.CreateVersion7();
        var app = new FeishuApp
        {
            Id = appId,
            Name = "App",
            AppId = "cli_test",
            CreateBy = "user-1",
        };
        var binding = new FeishuIdentityBinding
        {
            RootId = appId,
            OpenId = "ou_1",
            UserId = "user-1",
        };

        Assert.True(FeishuCallbackRules.MatchesIdentity(app, "ou_1", binding));
        Assert.False(FeishuCallbackRules.MatchesIdentity(app, "ou_2", binding));
        Assert.False(
            FeishuCallbackRules.MatchesIdentity(
                app,
                "ou_1",
                new FeishuIdentityBinding
                {
                    RootId = appId,
                    OpenId = "ou_1",
                    UserId = "user-2",
                }
            )
        );
        Assert.False(
            FeishuCallbackRules.MatchesIdentity(
                app,
                "ou_1",
                new FeishuIdentityBinding
                {
                    RootId = Guid.CreateVersion7(),
                    OpenId = "ou_1",
                    UserId = "user-1",
                }
            )
        );
        Assert.False(FeishuCallbackRules.MatchesIdentity(app, "ou_1", null));
    }
}
