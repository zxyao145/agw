using Agw.Extensions.Sdk.Database;

namespace Agw.Extensions.Feishu;

public enum FeishuAppMode
{
    Webhook,
    App,
}

public enum FeishuTargetKind
{
    Chat,
    User,
}

public enum FeishuCallbackState
{
    Processing,
    Completed,
    Rejected,
}

public abstract class FeishuOwnedEntity : IExtensionOwnedEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string? CreateBy { get; set; }

    public DateTimeOffset CreateTime { get; set; }

    public string? UpdateBy { get; set; }

    public DateTimeOffset? UpdateTime { get; set; }
}

public abstract class FeishuAppChild : FeishuOwnedEntity, IExtensionOwnedChild<FeishuApp, Guid>
{
    public Guid RootId { get; set; }
}

public sealed class FeishuApp : FeishuOwnedEntity
{
    public required string Name { get; set; }

    public FeishuAppMode Mode { get; set; }

    public string OpenDomain { get; set; } = FeishuDomains.DefaultOpenDomain;

    public string? WebhookUrl { get; set; }

    public string? WebhookSecret { get; set; }

    public string? AppId { get; set; }

    public string? AppSecret { get; set; }

    public string? EncryptKey { get; set; }

    public string? VerificationToken { get; set; }

    public long Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;
}

public sealed class FeishuTarget : FeishuAppChild
{
    public FeishuTargetKind Kind { get; set; }

    public required string ReceiveId { get; set; }

    public required string Name { get; set; }
}

public sealed class FeishuIdentityBinding : FeishuAppChild
{
    public required string OpenId { get; set; }

    public required string UserId { get; set; }
}

public sealed class FeishuBindRequest : FeishuAppChild
{
    public required string CodeHash { get; set; }

    public string? OpenId { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class FeishuMessage : FeishuAppChild
{
    public required string DispatchKey { get; set; }

    public Guid NotificationId { get; set; }

    public Guid TargetId { get; set; }

    public required string MessageId { get; set; }

    public long CardVersion { get; set; }
}

public sealed class FeishuCallback : FeishuAppChild
{
    public required string EventId { get; set; }

    public required string PayloadJson { get; set; }

    public FeishuCallbackState State { get; set; }

    public string? OpenId { get; set; }

    public Guid? NotificationId { get; set; }

    public Guid? TargetId { get; set; }

    public string? MessageId { get; set; }

    public string? UserId { get; set; }

    public Guid? OutboxId { get; set; }

    public int? Option { get; set; }

    public string? Text { get; set; }
}
