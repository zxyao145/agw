using System.Text.Json;
using Agw.Extensions.Sdk;
using Agw.Notifications.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Extensions.Feishu;

public sealed class FeishuOutboxResultHandler : IExtensionOutboxResultHandler
{
    private readonly FeishuDbContext _db;
    private readonly FeishuRuntimeState _runtimeState;
    private readonly FeishuNodeBridge _nodeBridge;

    public FeishuOutboxResultHandler(FeishuDbContext db, FeishuRuntimeState runtimeState, FeishuNodeBridge nodeBridge)
    {
        _db = db;
        _runtimeState = runtimeState;
        _nodeBridge = nodeBridge;
    }

    public async ValueTask HandleAsync(ExtensionOutboxResult result, CancellationToken cancellationToken)
    {
        FeishuCallback? callback = await _db.Callbacks.SingleOrDefaultAsync(
            row => row.OutboxId == result.Id,
            cancellationToken
        );
        if (callback is null)
            return;

        string title;
        string body;
        if (result.State == ExtensionOutboxState.Completed)
        {
            callback.State = FeishuCallbackState.Completed;
            NotificationAnswerResult? answer = result.ResultJson is null
                ? null
                : JsonSerializer.Deserialize(result.ResultJson, FeishuApiJsonContext.Default.NotificationAnswerResult);
            title = answer?.State is NotificationState.Answered or NotificationState.Resolved
                ? "Answer accepted"
                : "Question updated";
            body = "The notification has been updated in Agw.";
        }
        else
        {
            callback.State = FeishuCallbackState.Rejected;
            (title, body) = result.ErrorCode switch
            {
                var code when code == ErrorCodes.NotificationQuestionAlreadyAnswered.Code => (
                    "Already answered",
                    "This question has already been answered."
                ),
                var code when code == ErrorCodes.NotificationAnswerForbidden.Code => (
                    "Answer not permitted",
                    "Your Agw account is not permitted to answer this question."
                ),
                _ => ("Answer rejected", "Agw could not accept this answer."),
            };
        }

        FeishuMessage? message = callback.MessageId is null
            ? null
            : await _db.Messages.SingleOrDefaultAsync(
                row => row.RootId == callback.RootId && row.MessageId == callback.MessageId,
                cancellationToken
            );
        FeishuApp? app = message is null
            ? null
            : await _db.Apps.SingleOrDefaultAsync(row => row.Id == callback.RootId, cancellationToken);
        if (message is not null && app is { Mode: FeishuAppMode.App, Enabled: true })
        {
            await _nodeBridge.UpdateApplicationCardAsync(
                _runtimeState.ToNodeApplication(app),
                message.MessageId,
                FeishuCards.CallbackCard(title, body),
                cancellationToken
            );
        }
        await _db.SaveChangesAsync(cancellationToken);
    }
}
