using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Contracts;
using Agw.Extensions.Contracts;
using Agw.Notifications.Application.Persistence;
using Agw.Notifications.Configuration;
using Agw.Notifications.Contracts;
using Agw.Notifications.Domain.Behaviors;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Notifications;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ContractNotificationDispatchAttemptResult = Agw.Notifications.Contracts.NotificationDispatchAttemptResult;
using ContractNotificationDispatchState = Agw.Notifications.Contracts.NotificationDispatchState;
using ContractNotificationKind = Agw.Notifications.Contracts.NotificationKind;
using ContractNotificationOption = Agw.Notifications.Contracts.NotificationOption;
using ContractNotificationState = Agw.Notifications.Contracts.NotificationState;
using EntityNotificationDispatchState = Agw.Shared.Data.Entities.Notifications.NotificationDispatchState;
using EntityNotificationKind = Agw.Shared.Data.Entities.Notifications.NotificationKind;
using EntityNotificationState = Agw.Shared.Data.Entities.Notifications.NotificationState;

namespace Agw.Notifications.Application;

public sealed class NotificationAppService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly INotificationsDbContext _dbContext;
    private readonly INotificationTransaction _transaction;
    private readonly IEventContextFacade _eventContext;
    private readonly NotificationChannelCatalog? _channelCatalog;
    private readonly IExtensionCallerAccessor? _callerAccessor;
    private readonly TimeProvider _timeProvider;
    private readonly NotificationDispatchOptions _dispatchOptions;

    public NotificationAppService(
        INotificationsDbContext dbContext,
        INotificationTransaction transaction,
        IEventContextFacade eventContext,
        TimeProvider timeProvider,
        IOptions<NotificationDispatchOptions> dispatchOptions,
        IExtensionCallerAccessor? callerAccessor = null,
        NotificationChannelCatalog? channelCatalog = null
    )
    {
        _dbContext = dbContext;
        _transaction = transaction;
        _eventContext = eventContext;
        _channelCatalog = channelCatalog;
        _timeProvider = timeProvider;
        _dispatchOptions = dispatchOptions.Value;
        _callerAccessor = callerAccessor;
    }

    public Task<Guid> CreateAsync(NotificationRequest request, CancellationToken cancellationToken = default) =>
        CreateCoreAsync(request, save: true, cancellationToken);

    public Task<Guid> StageAsync(NotificationRequest request, CancellationToken cancellationToken = default) =>
        CreateCoreAsync(request, save: false, cancellationToken);

    private async Task<Guid> CreateCoreAsync(
        NotificationRequest request,
        bool save,
        CancellationToken cancellationToken
    )
    {
        Validate(request);
        var ownerUserId = UserInfoUtil.RequiredUserId;
        var caller = _callerAccessor?.Current;
        EnsureCallerOwner(caller, ownerUserId);
        var callerKey = GetCallerKey(caller);
        var existing = await _dbContext
            .Notifications.AsNoTracking()
            .SingleOrDefaultAsync(
                notification =>
                    notification.CreateBy == ownerUserId
                    && notification.CallerKey == callerKey
                    && notification.IdempotencyKey == request.IdempotencyKey,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (existing is not null)
            return existing.Id;
        var extensionChannelKeys = request.ChannelKeys.Where(channelKey => channelKey != "desktop").ToArray();
        if (extensionChannelKeys.Length > 0)
        {
            var availableKeys = (
                await (_channelCatalog ?? throw new AgwException(ErrorCodes.ExtensionUnavailable))
                    .ListAsync(ownerUserId, cancellationToken)
                    .ConfigureAwait(false)
            )
                .Select(channel => channel.ChannelKey)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var channelKey in extensionChannelKeys)
            {
                var extensionId = ParseExtensionChannel(channelKey).ExtensionId;
                if (caller is not null && caller.ExtensionId != extensionId)
                    throw new AgwException(ErrorCodes.NotificationAnswerForbidden);
                if (!availableKeys.Contains(channelKey))
                    throw new AgwException(ErrorCodes.InvalidParam);
            }
        }
        var notificationId = Guid.CreateVersion7();
        EventContext? parentContext = request.ParentEventId is Guid parentEventId
            ? await _eventContext.GetAsync(parentEventId, cancellationToken).ConfigureAwait(false)
            : null;
        if (request.ParentEventId.HasValue && parentContext is null)
            throw new AgwException(ErrorCodes.InvalidParam, "Parent event was not found.");

        var now = _timeProvider.GetUtcNow();
        var notification = new Notification
        {
            Id = notificationId,
            CreateBy = ownerUserId,
            Kind = (EntityNotificationKind)request.Kind,
            Title = request.Title,
            Text = request.Text,
            Link = request.Link,
            OriginExtensionId = caller?.ExtensionId,
            CallerKey = callerKey,
            IdempotencyKey = request.IdempotencyKey,
            CorrelationKey = request.CorrelationKey,
            ChainId = parentContext?.ChainId ?? notificationId,
            ParentEventId = request.ParentEventId,
            Hops = parentContext?.Hops ?? 0,
            State = EntityNotificationState.Open,
            ChangedAt = now,
            Version = 1,
        };
        var question =
            request.Kind == ContractNotificationKind.Question
                ? new NotificationQuestion
                {
                    NotificationId = notificationId,
                    OptionsJson = JsonSerializer.Serialize(request.Options, JsonOptions),
                    Recommend = request.Recommend,
                    AllowText = request.AllowText,
                }
                : null;
        var dispatches = request
            .ChannelKeys.Select(channelKey => new NotificationDispatch
            {
                Id = Guid.CreateVersion7(),
                NotificationId = notificationId,
                ChannelKey = channelKey,
                State = EntityNotificationDispatchState.Pending,
                MessageVersion = notification.Version,
                SentVersion = 0,
                RecoverAfter = now.Add(_dispatchOptions.RecoveryStaleAfter),
            })
            .ToArray();

        var result = save
            ? await _transaction
                .CreateAsync(notification, question, dispatches, cancellationToken)
                .ConfigureAwait(false)
            : await _transaction
                .StageCreateAsync(notification, question, dispatches, cancellationToken)
                .ConfigureAwait(false);
        return result.NotificationId;
    }

    public async Task UpdateAsync(NotificationUpdate update, CancellationToken cancellationToken = default)
    {
        Validate(update);
        var ownerUserId = UserInfoUtil.RequiredUserId;
        var caller = _callerAccessor?.Current;
        EnsureCallerOwner(caller, ownerUserId);
        var callerKey = GetCallerKey(caller);
        var originExtensionId = caller?.ExtensionId;
        var requestHash = CreateUpdateRequestHash(update);
        var notification = await _dbContext
            .Notifications.FirstOrDefaultAsync(
                notification =>
                    notification.Id == update.NotificationId
                    && notification.CreateBy == ownerUserId
                    && notification.CallerKey == callerKey
                    && notification.OriginExtensionId == originExtensionId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (notification is null)
            throw new AgwException(ErrorCodes.NotificationNotFound);

        var receipt = await FindUpdateReceiptAsync(update.NotificationId, update.Version, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is not null)
        {
            if (string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal))
                return;
            throw new AgwException(ErrorCodes.InvalidParam);
        }

        if (notification.Version != update.Version || notification.State == EntityNotificationState.Closed)
            throw new AgwException(ErrorCodes.InvalidParam);

        var now = _timeProvider.GetUtcNow();
        notification.Title = update.Title;
        notification.Text = update.Text;
        notification.Link = update.Link;
        notification.State = (EntityNotificationState)update.State;
        notification.Version++;
        notification.ChangedAt = now;
        _dbContext.NotificationUpdateReceipts.Add(
            new NotificationUpdateReceipt
            {
                NotificationId = notification.Id,
                RequestVersion = update.Version,
                RequestHash = requestHash,
            }
        );
        await UpdateDispatchVersionsAsync(notification, now, cancellationToken).ConfigureAwait(false);
        await SaveUpdateReceiptAsync(update, requestHash, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationAnswerResult> SubmitAnswerAsync(
        NotificationAnswer answer,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = UserInfoUtil.RequiredUserId;
        var caller = _callerAccessor?.Current;
        EnsureCallerOwner(caller, ownerUserId);
        var notification = await _dbContext
            .Notifications.FirstOrDefaultAsync(
                notification => notification.Id == answer.NotificationId && notification.CreateBy == ownerUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (notification is null)
            throw new AgwException(ErrorCodes.ResourceNotFound);
        var question = await _dbContext
            .NotificationQuestions.FirstOrDefaultAsync(
                question => question.NotificationId == notification.Id,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (question is null)
            throw new AgwException(ErrorCodes.NotificationNotFound);
        if (notification.State == EntityNotificationState.Answered)
            throw AlreadyAnswered(notification, question);
        if (notification.Kind != EntityNotificationKind.Question || notification.State != EntityNotificationState.Open)
            throw new AgwException(ErrorCodes.NotificationNotFound);

        var answeredVia = ValidateAnswerChannel(answer, caller);
        if (answer.ChannelKey is not null)
        {
            var dispatchExists = await _dbContext
                .NotificationDispatches.AsNoTracking()
                .AnyAsync(
                    dispatch =>
                        dispatch.NotificationId == notification.Id
                        && dispatch.ChannelKey == answer.ChannelKey
                        && dispatch.ExternalMessageId == answer.ExternalMessageId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!dispatchExists)
                throw new AgwException(ErrorCodes.NotificationAnswerForbidden);
        }

        var now = _timeProvider.GetUtcNow();
        notification.Question = question;
        var optionCount =
            JsonSerializer.Deserialize<ContractNotificationOption[]>(question.OptionsJson, JsonOptions)?.Length ?? 0;
        new NotificationBehavior(notification).Answer(
            answer.Option,
            answer.Text,
            optionCount,
            ownerUserId,
            answeredVia,
            now
        );
        await UpdateDispatchVersionsAsync(notification, now, cancellationToken).ConfigureAwait(false);
        var eventData = new
        {
            notificationId = notification.Id,
            origin = notification.OriginExtensionId?.ToString("D"),
            correlationKey = notification.CorrelationKey,
            option = question.AnswerOption,
            text = question.AnswerText,
            answeredBy = question.AnsweredBy,
            answeredVia = question.AnsweredVia,
            answeredAt = question.AnsweredAt,
        };
        var eventJson = JsonSerializer.SerializeToElement(eventData, JsonOptions);
        var eventRequest = new EventStagingRequest
        {
            PublisherKey = "agw.notifications",
            Event = new CloudEventDraft
            {
                Id = $"{notification.Id:D}:answered",
                Type = "agw.notification.answered",
                Source = "/agw/notifications",
                Time = now,
                Subject = notification.Id.ToString("D"),
                DataContentType = "application/json",
                Data = new CloudEventData
                {
                    Kind = CloudEventDataKind.Json,
                    Text = eventJson.GetRawText(),
                    Json = eventJson,
                },
                ParentEventId = notification.ParentEventId,
            },
            InheritParentHops = notification.ParentEventId.HasValue,
            ChainId = notification.ParentEventId.HasValue ? null : notification.ChainId,
            Hops = notification.ParentEventId.HasValue ? null : notification.Hops,
        };
        await _transaction.StageEventAsync(eventRequest, cancellationToken).ConfigureAwait(false);

        try
        {
            await _transaction.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            var currentNotification = await _dbContext
                .Notifications.AsNoTracking()
                .FirstOrDefaultAsync(
                    current => current.Id == answer.NotificationId && current.CreateBy == ownerUserId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            var currentQuestion = currentNotification is null
                ? null
                : await _dbContext
                    .NotificationQuestions.AsNoTracking()
                    .FirstOrDefaultAsync(current => current.NotificationId == answer.NotificationId, cancellationToken)
                    .ConfigureAwait(false);
            if (currentNotification?.State == EntityNotificationState.Answered && currentQuestion is not null)
                throw AlreadyAnswered(currentNotification, currentQuestion);
            throw;
        }

        return ToAnswerResult(notification, question);
    }

    public async Task<IReadOnlyList<NotificationListItem>> ListAsync(
        bool unreadOnly,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = UserInfoUtil.RequiredUserId;
        var query = _dbContext.Notifications.AsNoTracking().Where(notification => notification.CreateBy == ownerUserId);
        if (unreadOnly)
            query = query.Where(notification => notification.ReadAt == null);

        var notifications = await query
            .OrderByDescending(notification => notification.ChangedAt)
            .ThenByDescending(notification => notification.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return notifications.Select(ToListItem).ToArray();
    }

    public async Task<NotificationDetail?> GetAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        var ownerUserId = UserInfoUtil.RequiredUserId;
        var notification = await _dbContext
            .Notifications.AsNoTracking()
            .FirstOrDefaultAsync(
                notification => notification.Id == notificationId && notification.CreateBy == ownerUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (notification is null)
            return null;

        var question =
            notification.Kind == EntityNotificationKind.Question
                ? await _dbContext
                    .NotificationQuestions.AsNoTracking()
                    .FirstOrDefaultAsync(question => question.NotificationId == notification.Id, cancellationToken)
                    .ConfigureAwait(false)
                : null;
        var dispatches = await _dbContext
            .NotificationDispatches.AsNoTracking()
            .Where(dispatch => dispatch.NotificationId == notification.Id)
            .OrderBy(dispatch => dispatch.ChannelKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var dispatchIds = dispatches.Select(dispatch => dispatch.Id).ToArray();
        var attempts = await _dbContext
            .NotificationDispatchAttempts.AsNoTracking()
            .Where(attempt => dispatchIds.Contains(attempt.DispatchId))
            .OrderBy(attempt => attempt.StartedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new NotificationDetail
        {
            Id = notification.Id,
            Kind = (ContractNotificationKind)notification.Kind,
            State = (ContractNotificationState)notification.State,
            Title = notification.Title,
            Text = notification.Text,
            Link = notification.Link,
            ReadAt = notification.ReadAt,
            ChangedAt = notification.ChangedAt,
            Version = notification.Version,
            Question = question is null ? null : ToQuestionResponse(question),
            Dispatches = dispatches.Select(dispatch => ToDispatchResponse(dispatch, attempts)).ToArray(),
        };
    }

    public async Task MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        var ownerUserId = UserInfoUtil.RequiredUserId;
        var notification = await _dbContext
            .Notifications.FirstOrDefaultAsync(
                notification => notification.Id == notificationId && notification.CreateBy == ownerUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (notification is null)
            throw new AgwException(ErrorCodes.NotificationNotFound);
        if (notification.ReadAt.HasValue)
            return;

        notification.ReadAt = _timeProvider.GetUtcNow();
        notification.ChangedAt = notification.ReadAt.Value;
        await _transaction.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkDesktopDeliveredAsync(
        Guid notificationId,
        long version,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = UserInfoUtil.RequiredUserId;
        var notification = await _dbContext
            .Notifications.AsNoTracking()
            .FirstOrDefaultAsync(
                notification => notification.Id == notificationId && notification.CreateBy == ownerUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (notification is null)
            throw new AgwException(ErrorCodes.NotificationNotFound);

        var dispatch = await _dbContext
            .NotificationDispatches.FirstOrDefaultAsync(
                dispatch => dispatch.NotificationId == notificationId && dispatch.ChannelKey == "desktop",
                cancellationToken
            )
            .ConfigureAwait(false);
        if (dispatch is null || version > dispatch.MessageVersion)
            throw new AgwException(ErrorCodes.NotificationNotFound);
        if (version > dispatch.SentVersion)
            dispatch.SentVersion = version;
        if (dispatch.SentVersion == dispatch.MessageVersion)
            dispatch.State = EntityNotificationDispatchState.Sent;
        dispatch.RecoverAfter = _timeProvider.GetUtcNow().Add(_dispatchOptions.RecoveryStaleAfter);
        await _transaction.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RetryDispatchAsync(Guid dispatchId, CancellationToken cancellationToken = default)
    {
        var ownerUserId = UserInfoUtil.RequiredUserId;
        var dispatch = await _dbContext
            .NotificationDispatches.FirstOrDefaultAsync(
                dispatch =>
                    dispatch.Id == dispatchId
                    && dispatch.ChannelKey != "desktop"
                    && _dbContext.Notifications.Any(notification =>
                        notification.Id == dispatch.NotificationId && notification.CreateBy == ownerUserId
                    ),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (dispatch is null)
            throw new AgwException(ErrorCodes.NotificationNotFound);
        if (dispatch.State != EntityNotificationDispatchState.Parked)
            throw new AgwException(ErrorCodes.InvalidParam);

        var now = _timeProvider.GetUtcNow();
        dispatch.State = EntityNotificationDispatchState.Pending;
        dispatch.Attempt = 0;
        dispatch.NextAttemptAt = now;
        dispatch.LastError = null;
        dispatch.ClaimedBy = null;
        dispatch.ClaimExpiresAt = null;
        dispatch.RecoverAfter = now.Add(_dispatchOptions.RecoveryStaleAfter);
        _transaction.StageDispatch(dispatch);
        await _transaction.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<NotificationChannelResponse>> ListChannelsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = UserInfoUtil.RequiredUserId;
        return (_channelCatalog ?? throw new AgwException(ErrorCodes.ExtensionUnavailable)).ListAsync(
            ownerUserId,
            cancellationToken
        );
    }

    private async Task UpdateDispatchVersionsAsync(
        Notification notification,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken
    )
    {
        var dispatches = await _dbContext
            .NotificationDispatches.Where(dispatch => dispatch.NotificationId == notification.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var dispatch in dispatches)
        {
            dispatch.MessageVersion = notification.Version;
            if (
                dispatch.ChannelKey != "desktop"
                && dispatch.ExternalMessageId is not null
                && dispatch.State is EntityNotificationDispatchState.Sent or EntityNotificationDispatchState.Failed
            )
            {
                dispatch.State = EntityNotificationDispatchState.UpdatePending;
                dispatch.NextAttemptAt = null;
                dispatch.LastError = null;
                dispatch.RecoverAfter = changedAt.Add(_dispatchOptions.RecoveryStaleAfter);
                _transaction.StageDispatch(dispatch);
            }
        }
    }

    private Task<NotificationUpdateReceipt?> FindUpdateReceiptAsync(
        Guid notificationId,
        long requestVersion,
        CancellationToken cancellationToken
    ) =>
        _dbContext
            .NotificationUpdateReceipts.AsNoTracking()
            .SingleOrDefaultAsync(
                receipt => receipt.NotificationId == notificationId && receipt.RequestVersion == requestVersion,
                cancellationToken
            );

    private async Task SaveUpdateReceiptAsync(
        NotificationUpdate update,
        string requestHash,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _transaction.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (await HasMatchingUpdateReceiptAsync(update, requestHash, cancellationToken).ConfigureAwait(false))
                return;
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        catch (DbUpdateException)
        {
            if (await HasMatchingUpdateReceiptAsync(update, requestHash, cancellationToken).ConfigureAwait(false))
                return;
            throw;
        }
    }

    private async Task<bool> HasMatchingUpdateReceiptAsync(
        NotificationUpdate update,
        string requestHash,
        CancellationToken cancellationToken
    )
    {
        var receipt = await FindUpdateReceiptAsync(update.NotificationId, update.Version, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null)
            return false;
        if (!string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal))
            throw new AgwException(ErrorCodes.InvalidParam);
        return true;
    }

    private static string CreateUpdateRequestHash(NotificationUpdate update) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(update, JsonOptions))));

    private string ValidateAnswerChannel(NotificationAnswer answer, ExtensionCaller? caller)
    {
        var hasChannelKey = !string.IsNullOrWhiteSpace(answer.ChannelKey);
        var hasExternalMessageId = !string.IsNullOrWhiteSpace(answer.ExternalMessageId);
        if (hasChannelKey != hasExternalMessageId)
            throw new AgwException(ErrorCodes.NotificationAnswerForbidden);
        if (!hasChannelKey)
        {
            if (caller is not null)
                throw new AgwException(ErrorCodes.NotificationAnswerForbidden);
            return "desktop";
        }

        if (caller is null || !IsExtensionChannelFor(answer.ChannelKey!, caller.ExtensionId))
            throw new AgwException(ErrorCodes.NotificationAnswerForbidden);
        return answer.ChannelKey!;
    }

    private static bool IsExtensionChannelFor(string channelKey, Guid extensionId)
    {
        var parts = channelKey.Split(':', 4);
        return parts.Length == 4
            && parts[0] == "ext"
            && Guid.TryParse(parts[1], out var channelExtensionId)
            && channelExtensionId == extensionId
            && !string.IsNullOrWhiteSpace(parts[2])
            && !string.IsNullOrWhiteSpace(parts[3]);
    }

    private static (Guid ExtensionId, string ChannelId, string TargetId) ParseExtensionChannel(string channelKey)
    {
        var parts = channelKey.Split(':', 4);
        if (
            parts.Length != 4
            || parts[0] != "ext"
            || !Guid.TryParse(parts[1], out var extensionId)
            || string.IsNullOrWhiteSpace(parts[2])
            || string.IsNullOrWhiteSpace(parts[3])
        )
            throw new AgwException(ErrorCodes.InvalidParam);
        return (extensionId, parts[2], parts[3]);
    }

    private static string GetCallerKey(ExtensionCaller? caller) =>
        caller is null ? "agw.events" : $"ext:{caller.ExtensionId:D}";

    private static void EnsureCallerOwner(ExtensionCaller? caller, string ownerUserId)
    {
        if (caller?.UserId is { } callerUserId && !string.Equals(callerUserId, ownerUserId, StringComparison.Ordinal))
            throw new AgwException(ErrorCodes.NotificationAnswerForbidden);
    }

    private static void Validate(NotificationRequest request)
    {
        if (
            request is null
            || !Enum.IsDefined(request.Kind)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.IdempotencyKey.Length > 200
            || string.IsNullOrWhiteSpace(request.Title)
            || request.Title.Length > 200
            || string.IsNullOrWhiteSpace(request.Text)
            || request.Text.Length > 4000
            || request.Link?.Length > 2000
            || string.IsNullOrWhiteSpace(request.CorrelationKey)
            || request.CorrelationKey.Length > 200
            || request.ChannelKeys.Any(string.IsNullOrWhiteSpace)
            || request.ChannelKeys.Any(channel => channel.Length > 400)
            || request.ChannelKeys.Distinct(StringComparer.Ordinal).Count() != request.ChannelKeys.Count
        )
            throw new AgwException(ErrorCodes.InvalidParam);

        if (request.Kind == ContractNotificationKind.Question)
        {
            if (request.Options.Count == 0 && !request.AllowText)
                throw new AgwException(ErrorCodes.InvalidParam);
            if (request.Recommend is int recommend && (recommend < 0 || recommend >= request.Options.Count))
                throw new AgwException(ErrorCodes.InvalidParam);
            if (
                request.Options.Any(option =>
                    string.IsNullOrWhiteSpace(option.Label) || string.IsNullOrWhiteSpace(option.Value)
                )
            )
                throw new AgwException(ErrorCodes.InvalidParam);
        }
        else if (request.Options.Count != 0 || request.Recommend.HasValue || request.AllowText)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
    }

    private static void Validate(NotificationUpdate update)
    {
        if (
            update is null
            || update.Version < 1
            || !Enum.IsDefined(update.State)
            || update.State == ContractNotificationState.Answered
            || string.IsNullOrWhiteSpace(update.Title)
            || update.Title.Length > 200
            || string.IsNullOrWhiteSpace(update.Text)
            || update.Text.Length > 4000
            || update.Link?.Length > 2000
        )
            throw new AgwException(ErrorCodes.InvalidParam);
    }

    private static NotificationListItem ToListItem(Notification notification) =>
        new()
        {
            Id = notification.Id,
            Kind = (ContractNotificationKind)notification.Kind,
            State = (ContractNotificationState)notification.State,
            Title = notification.Title,
            Text = notification.Text,
            Link = notification.Link,
            ReadAt = notification.ReadAt,
            ChangedAt = notification.ChangedAt,
            Version = notification.Version,
        };

    private static NotificationQuestionResponse ToQuestionResponse(NotificationQuestion question) =>
        new()
        {
            Options = JsonSerializer.Deserialize<ContractNotificationOption[]>(question.OptionsJson, JsonOptions) ?? [],
            Recommend = question.Recommend,
            AllowText = question.AllowText,
            AnswerOption = question.AnswerOption,
            AnswerText = question.AnswerText,
            AnsweredBy = question.AnsweredBy,
            AnsweredVia = question.AnsweredVia,
            AnsweredAt = question.AnsweredAt,
        };

    private static AgwException AlreadyAnswered(Notification notification, NotificationQuestion question)
    {
        var exception = new AgwException(ErrorCodes.NotificationQuestionAlreadyAnswered);
        exception.Data[nameof(NotificationAnswerResult)] = ToAnswerResult(notification, question);
        return exception;
    }

    private static NotificationAnswerResult ToAnswerResult(Notification notification, NotificationQuestion question) =>
        new()
        {
            NotificationId = notification.Id,
            State = (ContractNotificationState)notification.State,
            Version = notification.Version,
            Option = question.AnswerOption,
            Text = question.AnswerText,
            AnsweredBy = question.AnsweredBy,
            AnsweredVia = question.AnsweredVia,
            AnsweredAt = question.AnsweredAt,
        };

    private static NotificationDispatchResponse ToDispatchResponse(
        NotificationDispatch dispatch,
        IReadOnlyList<NotificationDispatchAttempt> attempts
    ) =>
        new()
        {
            Id = dispatch.Id,
            ChannelKey = dispatch.ChannelKey,
            State = (ContractNotificationDispatchState)dispatch.State,
            ExternalMessageId = dispatch.ExternalMessageId,
            MessageVersion = dispatch.MessageVersion,
            SentVersion = dispatch.SentVersion,
            Attempt = dispatch.Attempt,
            NextAttemptAt = dispatch.NextAttemptAt,
            LastError = dispatch.LastError,
            Attempts = attempts
                .Where(attempt => attempt.DispatchId == dispatch.Id)
                .Select(attempt => new NotificationDispatchAttemptResponse
                {
                    Id = attempt.Id,
                    Attempt = attempt.Attempt,
                    MessageVersion = attempt.MessageVersion,
                    StartedAt = attempt.StartedAt,
                    FinishedAt = attempt.FinishedAt,
                    Result = (ContractNotificationDispatchAttemptResult)attempt.Result,
                    ExternalMessageId = attempt.ExternalMessageId,
                    Error = attempt.Error,
                })
                .ToArray(),
        };
}
