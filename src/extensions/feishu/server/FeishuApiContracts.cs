namespace Agw.Extensions.Feishu;

public sealed record FeishuAppWriteRequest
{
    public Guid? Id { get; init; }

    public long? Version { get; init; }

    public required string Name { get; init; }

    public required string Mode { get; init; }

    public string? OpenDomain { get; init; }

    public string? WebhookUrl { get; init; }

    public string? WebhookSecret { get; init; }

    public string? AppId { get; init; }

    public string? AppSecret { get; init; }
}

public sealed record FeishuAppVersionRequest
{
    public required Guid Id { get; init; }

    public required long Version { get; init; }
}

public sealed record FeishuTargetSaveRequest
{
    public Guid? Id { get; init; }

    public required Guid AppId { get; init; }

    public required string Kind { get; init; }

    public required string ReceiveId { get; init; }

    public required string Name { get; init; }
}

public sealed record FeishuTargetDeleteRequest
{
    public required Guid Id { get; init; }

    public required Guid AppId { get; init; }
}

public sealed record FeishuAppIdRequest
{
    public required Guid AppId { get; init; }
}

public sealed record FeishuBindStartRequest
{
    public required Guid AppId { get; init; }

    public string? OpenId { get; init; }
}

public sealed record FeishuBindConfirmRequest
{
    public required string Code { get; init; }
}

public sealed record FeishuBindDeleteRequest
{
    public required Guid AppId { get; init; }

    public required string OpenId { get; init; }
}

public sealed record FeishuTestSendRequest
{
    public required Guid AppId { get; init; }

    public required Guid TargetId { get; init; }
}

public sealed record FeishuAppResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Mode { get; init; }

    public required string OpenDomain { get; init; }

    public required bool HasWebhookUrl { get; init; }

    public required bool HasWebhookSecret { get; init; }

    public string? AppId { get; init; }

    public required bool HasAppSecret { get; init; }

    public required bool Enabled { get; init; }

    public required long Version { get; init; }

    public required string ConnectionStatus { get; init; }

    public string? LastError { get; init; }
}

public sealed record FeishuTargetResponse
{
    public required Guid Id { get; init; }

    public required Guid AppId { get; init; }

    public required string Kind { get; init; }

    public required string ReceiveId { get; init; }

    public required string Name { get; init; }
}

public sealed record FeishuIdentityBindingResponse
{
    public required Guid AppId { get; init; }

    public required string OpenId { get; init; }

    public required string UserId { get; init; }
}

public sealed record FeishuBindStartResponse
{
    public required string Code { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required string ConfirmationUrl { get; init; }
}

public sealed record FeishuVerificationResponse
{
    public required bool Valid { get; init; }

    public string? Error { get; init; }
}

public sealed record FeishuChatResponse
{
    public required string ReceiveId { get; init; }

    public required string Name { get; init; }
}

public sealed record FeishuNodeApplication
{
    public required string OpenDomain { get; init; }

    public required string AppId { get; init; }

    public required string AppSecret { get; init; }
}

public sealed record FeishuNodeVerification
{
    public required bool Valid { get; init; }

    public string? Error { get; init; }
}

public sealed record FeishuNodeChat
{
    public required string ReceiveId { get; init; }

    public required string Name { get; init; }
}

public sealed record FeishuNodeMessage
{
    public required string MessageId { get; init; }
}

public sealed record FeishuChannelTarget
{
    public required FeishuApp App { get; init; }

    public FeishuTarget? Target { get; init; }
}

public sealed record FeishuActionPayload
{
    public required string EventId { get; init; }

    public required string AppOpenId { get; init; }

    public required string MessageId { get; init; }

    public required string OpenId { get; init; }

    public required Guid NotificationId { get; init; }

    public required string DispatchKey { get; init; }

    public required int Option { get; init; }

    public string? Text { get; init; }
}

public sealed record FeishuNodeEvent
{
    public required string EventKey { get; init; }

    public string? RequestId { get; init; }

    public required System.Text.Json.JsonElement Payload { get; init; }
}

public sealed record FeishuNodeWebhookRequest
{
    public required string WebhookUrl { get; init; }

    public string? WebhookSecret { get; init; }

    public required System.Text.Json.JsonElement Message { get; init; }

    public required string DispatchKey { get; init; }
}

public sealed record FeishuEventMessage
{
    public required string AppOpenId { get; init; }

    public required string OpenId { get; init; }

    public required string ReceiveId { get; init; }

    public required FeishuTargetKind TargetKind { get; init; }

    public required string Text { get; init; }
}

public sealed record FeishuCardButtonValue
{
    public required Guid NotificationId { get; init; }

    public required string DispatchKey { get; init; }

    public required int Option { get; init; }
}

public sealed record FeishuCallbackAcknowledgement
{
    public required FeishuCallbackState State { get; init; }

    public required string Message { get; init; }

    public System.Text.Json.JsonElement? Card { get; init; }

    public System.Text.Json.JsonElement? Toast { get; init; }
}

public static class FeishuChannel
{
    public const string Id = "feishu";

    public static string ChannelKey(Guid extensionId, Guid targetId) => $"ext:{extensionId:D}:{Id}:{targetId:D}";

    public static bool TryParseTargetId(string channelKey, Guid extensionId, out Guid targetId)
    {
        targetId = default;
        string[] parts = channelKey.Split(':', 4);
        return parts.Length == 4
            && parts[0] == "ext"
            && Guid.TryParse(parts[1], out Guid channelExtensionId)
            && channelExtensionId == extensionId
            && parts[2] == Id
            && Guid.TryParse(parts[3], out targetId);
    }
}
