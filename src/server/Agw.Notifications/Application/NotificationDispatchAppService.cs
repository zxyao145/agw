using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Notifications.Application.Persistence;
using Agw.Notifications.Configuration;
using Agw.Notifications.Observability;
using Agw.Shared.Data.Entities.Notifications;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ExtensionNotificationOption = Agw.Extensions.Contracts.NotificationOption;

namespace Agw.Notifications.Application;

public sealed class NotificationDispatchAppService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly INotificationDispatchStore _dispatchStore;
    private readonly IExtensionGenerationCatalog _generations;
    private readonly IExtensionInvoker _invoker;
    private readonly IExtensionHostIdentity _hostIdentity;
    private readonly TimeProvider _timeProvider;
    private readonly NotificationDispatchOptions _options;
    private readonly ILogger<NotificationDispatchAppService> _logger;

    public NotificationDispatchAppService(
        INotificationDispatchStore dispatchStore,
        IExtensionGenerationCatalog generations,
        IExtensionInvoker invoker,
        IExtensionHostIdentity hostIdentity,
        TimeProvider timeProvider,
        IOptions<NotificationDispatchOptions> options,
        ILogger<NotificationDispatchAppService> logger
    )
    {
        _dispatchStore = dispatchStore;
        _generations = generations;
        _invoker = invoker;
        _hostIdentity = hostIdentity;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> DispatchAsync(Guid dispatchId, CancellationToken cancellationToken = default)
    {
        var hostId = _hostIdentity.HostId;
        var claim = await _dispatchStore.TryClaimAsync(dispatchId, hostId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
            return false;

        long started = _timeProvider.GetTimestamp();

        using var invocationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewalTask = RenewUntilDoneAsync(
            claim.Dispatch.Id,
            claim.AttemptId,
            hostId,
            invocationCancellation,
            renewalCancellation.Token
        );
        NotificationSendResult? result = null;
        string? error = null;
        try
        {
            result = await SendAsync(claim, invocationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            _logger.LogWarning(
                exception,
                "Notification dispatch failed for {NotificationId} on {ChannelKey} at attempt {Attempt}.",
                claim.Notification.Id,
                claim.Dispatch.ChannelKey,
                claim.Dispatch.Attempt
            );
        }
        finally
        {
            renewalCancellation.Cancel();
        }

        var ownsLease = await renewalTask.ConfigureAwait(false);
        if (!ownsLease)
            return false;

        var outcome =
            error is not null ? NotificationDispatchAttemptResult.Failed
            : result!.Status == NotificationSendStatus.Unsupported ? NotificationDispatchAttemptResult.Unsupported
            : NotificationDispatchAttemptResult.Sent;
        NotificationTelemetry.SendDuration.Record(_timeProvider.GetElapsedTime(started).TotalSeconds);
        NotificationTelemetry.Sends.Add(1, new KeyValuePair<string, object?>("outcome", outcome.ToString()));
        return await _dispatchStore
            .CompleteAsync(
                claim.Dispatch.Id,
                claim.AttemptId,
                hostId,
                claim.Dispatch.MessageVersion,
                outcome,
                result?.ExternalMessageId,
                error,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<NotificationSendResult> SendAsync(
        NotificationDispatchClaim claim,
        CancellationToken cancellationToken
    )
    {
        var parts = claim.Dispatch.ChannelKey.Split(':', 4);
        if (
            parts.Length != 4
            || parts[0] != "ext"
            || !Guid.TryParse(parts[1], out var extensionId)
            || string.IsNullOrWhiteSpace(parts[2])
            || string.IsNullOrWhiteSpace(parts[3])
        )
            throw new AgwException(ErrorCodes.InvalidParam, "Notification channel key is invalid.");

        await using var generation = await _generations
            .AcquireForExecutionAsync(extensionId, cancellationToken)
            .ConfigureAwait(false);
        if (!generation.Snapshot.NotificationChannels.Any(channel => channel.Id == parts[2]))
            throw new AgwException(ErrorCodes.ExtensionUnavailable);

        var options = claim.Question is null
            ? Array.Empty<ExtensionNotificationOption>()
            : JsonSerializer.Deserialize<ExtensionNotificationOption[]>(claim.Question.OptionsJson, JsonOptions) ?? [];
        var message = new NotificationMessage
        {
            NotificationId = claim.Notification.Id,
            DispatchKey = claim.Dispatch.Id.ToString("D"),
            ChannelKey = claim.Dispatch.ChannelKey,
            IdempotencyKey = $"{claim.Dispatch.Id:D}:{claim.Dispatch.MessageVersion}",
            ContentVersion = claim.Dispatch.MessageVersion,
            Kind = (ExtensionNotificationKind)claim.Notification.Kind,
            State = (ExtensionNotificationState)claim.Notification.State,
            Title = claim.Notification.Title,
            Body = claim.Notification.Text,
            Link = claim.Notification.Link,
            Options = options,
            Recommend = claim.Question?.Recommend,
            AllowText = claim.Question?.AllowText ?? false,
            AnswerOption = claim.Question?.AnswerOption,
            AnswerText = claim.Question?.AnswerText,
            AnsweredBy = claim.Question?.AnsweredBy,
            AnsweredVia = claim.Question?.AnsweredVia,
            AnsweredAt = claim.Question?.AnsweredAt,
        };
        var operation = claim.Dispatch.ExternalMessageId is null
            ? ExtensionNotificationChannelOperation.Send
            : ExtensionNotificationChannelOperation.Update;
        var result = await _invoker
            .InvokeNotificationChannelAsync(
                generation,
                new ExtensionNotificationChannelInvocation
                {
                    ChannelId = parts[2],
                    UserId = claim.Notification.CreateBy ?? throw new AgwException(ErrorCodes.NotificationNotFound),
                    InvocationId = claim.Dispatch.Id,
                    Operation = operation,
                    Message = message,
                    ExternalMessageId = claim.Dispatch.ExternalMessageId,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return result.SendResult ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
    }

    private async Task<bool> RenewUntilDoneAsync(
        Guid dispatchId,
        Guid attemptId,
        string hostId,
        CancellationTokenSource invocationCancellation,
        CancellationToken cancellationToken
    )
    {
        var delay = TimeSpan.FromTicks(Math.Max(1, _options.ClaimDuration.Ticks / 3));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return true;
            }

            if (await _dispatchStore.RenewAsync(dispatchId, attemptId, hostId, cancellationToken).ConfigureAwait(false))
                continue;
            invocationCancellation.Cancel();
            return false;
        }

        return true;
    }
}
