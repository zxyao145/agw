using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Extensions.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Extensions.Feishu;

public sealed class FeishuNotificationChannel : IExtensionNotificationChannel
{
    private readonly FeishuDbContext _db;
    private readonly FeishuNodeBridge _nodeBridge;
    private readonly FeishuRuntimeState _runtimeState;

    public FeishuNotificationChannel(FeishuDbContext db, FeishuNodeBridge nodeBridge, FeishuRuntimeState runtimeState)
    {
        _db = db;
        _nodeBridge = nodeBridge;
        _runtimeState = runtimeState;
    }

    public async ValueTask<IReadOnlyList<NotificationChannelTarget>> ListTargetsAsync(
        CancellationToken cancellationToken
    )
    {
        FeishuApp[] apps = await _db
            .Apps.AsNoTracking()
            .Where(app => app.Enabled)
            .OrderBy(app => app.Name)
            .ToArrayAsync(cancellationToken);
        var targets = new List<NotificationChannelTarget>();
        foreach (FeishuApp app in apps.Where(app => app.Mode == FeishuAppMode.Webhook))
        {
            targets.Add(new NotificationChannelTarget { TargetId = app.Id.ToString("D"), Name = app.Name });
        }

        FeishuTarget[] appTargets = await _db
            .Targets.AsNoTracking()
            .Where(target =>
                _db.Apps.Any(app => app.Id == target.RootId && app.Enabled && app.Mode == FeishuAppMode.App)
            )
            .OrderBy(target => target.Name)
            .ToArrayAsync(cancellationToken);
        var appNames = apps.ToDictionary(app => app.Id, app => app.Name);
        foreach (FeishuTarget target in appTargets)
        {
            targets.Add(
                new NotificationChannelTarget
                {
                    TargetId = target.Id.ToString("D"),
                    Name = $"{appNames[target.RootId]} / {target.Name}",
                }
            );
        }
        return targets;
    }

    public async ValueTask<NotificationSendResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken
    )
    {
        if (
            !FeishuChannel.TryParseTargetId(
                message.ChannelKey,
                _db.ExtensionDatabaseIdentity.ExtensionId,
                out Guid targetId
            )
        )
            throw new AgwException(ErrorCodes.InvalidParam);
        FeishuMessage? prior = await _db.Messages.SingleOrDefaultAsync(
            row => row.DispatchKey == message.DispatchKey,
            cancellationToken
        );
        if (prior is not null)
        {
            return new NotificationSendResult
            {
                Status = NotificationSendStatus.Sent,
                ExternalMessageId = prior.MessageId,
            };
        }

        FeishuTarget? target = await _db.Targets.SingleOrDefaultAsync(row => row.Id == targetId, cancellationToken);
        FeishuApp? app = target is null
            ? await _db.Apps.SingleOrDefaultAsync(row => row.Id == targetId, cancellationToken)
            : await _db.Apps.SingleOrDefaultAsync(row => row.Id == target.RootId, cancellationToken);
        if (app is null || !app.Enabled)
            throw new AgwException(ErrorCodes.ExtensionNotFound);

        if (app.Mode == FeishuAppMode.Webhook)
        {
            FeishuNodeMessage webhookResult = await SendWebhookAsync(app, message, cancellationToken);
            return new NotificationSendResult
            {
                Status = NotificationSendStatus.Sent,
                ExternalMessageId = webhookResult.MessageId,
            };
        }

        if (target is null)
            throw new AgwException(ErrorCodes.ExtensionNotFound);
        FeishuNodeMessage result = await _nodeBridge.SendApplicationMessageAsync(
            _runtimeState.ToNodeApplication(app),
            target.Kind,
            target.ReceiveId,
            message,
            cancellationToken
        );
        _db.Messages.Add(
            new FeishuMessage
            {
                RootId = app.Id,
                DispatchKey = message.DispatchKey,
                NotificationId = message.NotificationId,
                TargetId = target.Id,
                MessageId = result.MessageId,
                CardVersion = message.ContentVersion,
            }
        );
        await _db.SaveChangesAsync(cancellationToken);
        return new NotificationSendResult
        {
            Status = NotificationSendStatus.Sent,
            ExternalMessageId = result.MessageId,
        };
    }

    public async ValueTask<NotificationSendResult> UpdateAsync(
        NotificationMessage message,
        string externalMessageId,
        CancellationToken cancellationToken
    )
    {
        if (externalMessageId.StartsWith("webhook:", StringComparison.Ordinal))
            return new NotificationSendResult { Status = NotificationSendStatus.Unsupported };
        FeishuMessage? record = await _db.Messages.SingleOrDefaultAsync(
            row => row.MessageId == externalMessageId && row.DispatchKey == message.DispatchKey,
            cancellationToken
        );
        if (record is null)
            throw new AgwException(ErrorCodes.ExtensionNotFound);
        FeishuApp? app = await _db.Apps.SingleOrDefaultAsync(row => row.Id == record.RootId, cancellationToken);
        FeishuTarget? target = await _db.Targets.SingleOrDefaultAsync(
            row => row.Id == record.TargetId && row.RootId == record.RootId,
            cancellationToken
        );
        if (app is null || target is null || app.Mode != FeishuAppMode.App || !app.Enabled)
            throw new AgwException(ErrorCodes.ExtensionNotFound);

        await _nodeBridge.UpdateApplicationMessageAsync(
            _runtimeState.ToNodeApplication(app),
            record.MessageId,
            message,
            cancellationToken
        );
        record.CardVersion = message.ContentVersion;
        await _db.SaveChangesAsync(cancellationToken);
        return new NotificationSendResult
        {
            Status = NotificationSendStatus.Sent,
            ExternalMessageId = record.MessageId,
        };
    }

    public async Task<FeishuNodeMessage> SendForAppAsync(
        FeishuApp app,
        Guid targetId,
        NotificationMessage message,
        CancellationToken cancellationToken
    )
    {
        if (app.Mode == FeishuAppMode.Webhook)
        {
            if (targetId != app.Id)
                throw new AgwException(ErrorCodes.ExtensionNotFound);
            return await SendWebhookAsync(app, message, cancellationToken);
        }

        FeishuTarget target =
            await _db.Targets.SingleOrDefaultAsync(row => row.RootId == app.Id && row.Id == targetId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.ExtensionNotFound);
        FeishuNodeMessage result = await _nodeBridge.SendApplicationMessageAsync(
            _runtimeState.ToNodeApplication(app),
            target.Kind,
            target.ReceiveId,
            message,
            cancellationToken
        );
        _db.Messages.Add(
            new FeishuMessage
            {
                RootId = app.Id,
                DispatchKey = message.DispatchKey,
                NotificationId = message.NotificationId,
                TargetId = target.Id,
                MessageId = result.MessageId,
                CardVersion = message.ContentVersion,
            }
        );
        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<FeishuNodeMessage> SendWebhookAsync(
        FeishuApp app,
        NotificationMessage message,
        CancellationToken cancellationToken
    )
    {
        JsonElement card = FeishuCards.NotificationCard(message, message.Link, interactive: false);
        var payloadNode = new JsonObject { ["msg_type"] = "interactive", ["card"] = JsonNode.Parse(card.GetRawText()) };
        using JsonDocument payloadDocument = JsonDocument.Parse(payloadNode.ToJsonString());
        JsonElement payload = payloadDocument.RootElement.Clone();
        return await _nodeBridge.SendWebhookMessageAsync(
            new FeishuNodeWebhookRequest
            {
                WebhookUrl = _runtimeState.Unprotect(app.Id, app.WebhookUrl),
                WebhookSecret = _runtimeState.Unprotect(app.Id, app.WebhookSecret),
                Message = payload,
                DispatchKey = message.DispatchKey,
            },
            cancellationToken
        );
    }
}
