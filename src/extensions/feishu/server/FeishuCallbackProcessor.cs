using System.Text.Json;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Agw.Extensions.Feishu;

public static class FeishuCallbackProcessor
{
    public static async ValueTask<FeishuCallbackAcknowledgement> ProcessAsync(
        FeishuDbContext db,
        FeishuApp app,
        Guid extensionId,
        JsonSerializerOptions jsonOptions,
        JsonElement payload,
        CancellationToken cancellationToken
    )
    {
        if (!TryReadAction(payload, out FeishuActionPayload action))
        {
            return Rejected("Invalid operation.", "This action is no longer valid.");
        }

        FeishuCallback? previous = await db
            .Callbacks.AsNoTracking()
            .SingleOrDefaultAsync(row => row.EventId == action.EventId, cancellationToken);
        if (previous is not null)
            return Acknowledgement(previous.State);

        var callback = new FeishuCallback
        {
            RootId = app.Id,
            EventId = action.EventId,
            PayloadJson = payload.GetRawText(),
            State = FeishuCallbackState.Rejected,
            OpenId = action.OpenId,
            NotificationId = action.NotificationId,
            MessageId = action.MessageId,
        };
        db.Callbacks.Add(callback);

        bool appMatches = string.Equals(action.AppOpenId, app.AppId, StringComparison.Ordinal);
        FeishuMessage? message = appMatches
            ? await db.Messages.SingleOrDefaultAsync(
                row => row.RootId == app.Id && row.MessageId == action.MessageId,
                cancellationToken
            )
            : null;
        FeishuTarget? target = message is null
            ? null
            : await db.Targets.SingleOrDefaultAsync(
                row => row.RootId == app.Id && row.Id == message.TargetId,
                cancellationToken
            );
        FeishuIdentityBinding? binding = await db.IdentityBindings.SingleOrDefaultAsync(
            row => row.RootId == app.Id && row.OpenId == action.OpenId,
            cancellationToken
        );
        bool messageMatches = FeishuCallbackRules.MatchesMessage(
            app,
            action.AppOpenId,
            action.MessageId,
            action.NotificationId,
            action.DispatchKey,
            message,
            target
        );
        if (!messageMatches)
        {
            await db.SaveChangesAsync(cancellationToken);
            return Rejected("Invalid operation.", "This action is no longer valid.");
        }

        FeishuMessage verifiedMessage = message!;
        callback.TargetId = verifiedMessage.TargetId;
        if (!FeishuCallbackRules.MatchesIdentity(app, action.OpenId, binding))
        {
            await db.SaveChangesAsync(cancellationToken);
            return Rejected(
                "This Feishu account is not linked to Agw.",
                "Link this Feishu account in Agw before answering."
            );
        }

        callback.State = FeishuCallbackState.Processing;
        callback.UserId = binding!.UserId;
        callback.Option = action.Option;
        callback.Text = action.Text;
        callback.OutboxId = ExtensionOutbox.Enqueue(
            db,
            ExtensionOutboxKind.SubmitAnswer,
            $"feishu:{app.Id:D}:{action.EventId}",
            new NotificationAnswer
            {
                NotificationId = action.NotificationId,
                Option = action.Option,
                Text = action.Text,
                ChannelKey = FeishuChannel.ChannelKey(extensionId, verifiedMessage.TargetId),
                ExternalMessageId = action.MessageId,
            },
            jsonOptions
        );
        await db.SaveChangesAsync(cancellationToken);
        return new FeishuCallbackAcknowledgement
        {
            State = FeishuCallbackState.Processing,
            Message = "Received. Processing the answer.",
            Card = FeishuCards.CallbackCard("Processing", "Your answer was received and is being processed."),
            Toast = FeishuCards.CallbackToast("success", "Received. Processing."),
        };
    }

    private static FeishuCallbackAcknowledgement Acknowledgement(FeishuCallbackState state)
    {
        string message = state switch
        {
            FeishuCallbackState.Completed => "The answer was processed.",
            FeishuCallbackState.Rejected => "This action was rejected.",
            _ => "Received. Processing the answer.",
        };
        string cardMessage = state switch
        {
            FeishuCallbackState.Completed => "Your answer was processed.",
            FeishuCallbackState.Rejected => "This action was rejected.",
            _ => "Your answer is being processed.",
        };
        return new FeishuCallbackAcknowledgement
        {
            State = state,
            Message = message,
            Card = FeishuCards.CallbackCard(state.ToString(), cardMessage),
            Toast = FeishuCards.CallbackToast(state == FeishuCallbackState.Rejected ? "error" : "success", message),
        };
    }

    private static FeishuCallbackAcknowledgement Rejected(string message, string cardMessage) =>
        new()
        {
            State = FeishuCallbackState.Rejected,
            Message = message,
            Card = FeishuCards.CallbackCard("Action rejected", cardMessage),
            Toast = FeishuCards.CallbackToast("error", message),
        };

    private static bool TryReadAction(JsonElement payload, out FeishuActionPayload action)
    {
        action = default!;
        if (!TryGetString(payload, "event_type", out string eventType) || eventType != "card.action.trigger")
            return false;
        if (!TryGetString(payload, "event_id", out string eventId))
            return false;
        if (!TryGetString(payload, "app_id", out string appOpenId))
            return false;
        if (!TryGetProperty(payload, "operator", out JsonElement operatorData))
            return false;
        if (!TryGetString(operatorData, "open_id", out string openId))
            return false;
        if (!TryGetProperty(payload, "context", out JsonElement contextData))
            return false;
        if (!TryGetString(contextData, "open_message_id", out string messageId))
            return false;
        if (!TryGetProperty(payload, "action", out JsonElement actionData))
            return false;
        if (!TryGetProperty(actionData, "value", out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            return false;
        if (!TryGetString(value, "notificationId", out string notificationIdText))
            return false;
        if (!Guid.TryParse(notificationIdText, out Guid notificationId))
            return false;
        if (!TryGetString(value, "dispatchKey", out string dispatchKey))
            return false;
        if (!TryGetProperty(value, "option", out JsonElement optionValue))
            return false;

        int option = optionValue.ValueKind switch
        {
            JsonValueKind.Number when optionValue.TryGetInt32(out int number) => number,
            JsonValueKind.String when int.TryParse(optionValue.GetString(), out int number) => number,
            _ => -1,
        };
        if (option < 0)
            return false;
        string? text = TryGetProperty(value, "text", out string actionText) ? actionText : null;
        action = new FeishuActionPayload
        {
            EventId = eventId,
            AppOpenId = appOpenId,
            MessageId = messageId,
            OpenId = openId,
            NotificationId = notificationId,
            DispatchKey = dispatchKey,
            Option = option,
            Text = text,
        };
        return true;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static bool TryGetProperty(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetString(JsonElement element, string name, out string value) =>
        TryGetProperty(element, name, out value);
}
