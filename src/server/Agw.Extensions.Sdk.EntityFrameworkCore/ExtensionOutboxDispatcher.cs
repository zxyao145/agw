using System.Text.Json;
using Agw.Agents.Contracts.Execution;
using Agw.Events.Contracts;
using Agw.Extensions.Sdk.Database;
using Agw.Notifications.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal sealed class ExtensionOutboxDispatcher<TDbContext> : IExtensionOutboxWorker
    where TDbContext : ExtensionDbContext
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExtensionOutboxDispatcher<TDbContext>> _logger;
    private IExtensionLeaseManager? _leases;
    private IExtensionBackgroundScheduler? _backgroundScheduler;
    private IExtensionUserBackgroundScheduler? _scheduler;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;

    public ExtensionOutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<ExtensionOutboxDispatcher<TDbContext>> logger
    )
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public ValueTask StartAsync(
        IExtensionContext context,
        IExtensionBackgroundScheduler scheduler,
        CancellationToken cancellationToken
    )
    {
        _leases = context.Leases;
        _backgroundScheduler = scheduler;
        _scheduler =
            scheduler as IExtensionUserBackgroundScheduler ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
        return ValueTask.CompletedTask;
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken) => StopLoopAsync();

    public ValueTask ResumeAsync(CancellationToken cancellationToken)
    {
        StartLoop(cancellationToken);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await StopLoopAsync();
        _leases = null;
        _backgroundScheduler = null;
        _scheduler = null;
    }

    private void StartLoop(CancellationToken cancellationToken)
    {
        if (_runCancellation is not null)
        {
            return;
        }

        IExtensionLeaseManager leases = _leases ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
        IExtensionBackgroundScheduler backgroundScheduler =
            _backgroundScheduler ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
        IExtensionUserBackgroundScheduler scheduler =
            _scheduler ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunAsync(leases, backgroundScheduler, scheduler, _runCancellation.Token);
    }

    private async ValueTask StopLoopAsync()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _runCancellation, null);
        Task? runTask = Interlocked.Exchange(ref _runTask, null);
        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync();
        try
        {
            if (runTask is not null)
            {
                await runTask;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _logger.LogDebug("Extension outbox dispatcher stopped");
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task RunAsync(
        IExtensionLeaseManager leases,
        IExtensionBackgroundScheduler backgroundScheduler,
        IExtensionUserBackgroundScheduler scheduler,
        CancellationToken cancellationToken
    )
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IExtensionLease lease;
                try
                {
                    lease = await leases.AcquireAsync("outbox", cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Extension outbox lease acquisition failed");
                    await Task.Delay(PollInterval, _timeProvider, cancellationToken);
                    continue;
                }

                await using (lease)
                {
                    using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        lease.HandleLostToken
                    );
                    try
                    {
                        await RunWithLeaseAsync(backgroundScheduler, scheduler, leaseCancellation.Token);
                    }
                    catch (OperationCanceledException) when (leaseCancellation.IsCancellationRequested)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }

                        _logger.LogDebug("Extension outbox dispatcher lease ended");
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(PollInterval, _timeProvider, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Extension outbox dispatcher stopped");
        }
    }

    private async Task RunWithLeaseAsync(
        IExtensionBackgroundScheduler backgroundScheduler,
        IExtensionUserBackgroundScheduler scheduler,
        CancellationToken cancellationToken
    )
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ExtensionOutboxWorkItem? work;
            try
            {
                work = await backgroundScheduler.ExecuteAsync<ExtensionOutboxWorkItem?>(
                    async (_, scanToken) => await FindNextAsync(scanToken),
                    cancellationToken
                );
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Extension outbox scan failed");
                await Task.Delay(PollInterval, _timeProvider, cancellationToken);
                continue;
            }

            if (work is null)
            {
                await Task.Delay(PollInterval, _timeProvider, cancellationToken);
                continue;
            }

            try
            {
                await scheduler.ExecuteAsync(
                    work.UserId,
                    work.Id,
                    (context, workToken) => DispatchAsync(work.Id, context, workToken),
                    cancellationToken
                );
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AgwException exception) when (exception.Code == ErrorCodes.ExtensionUnavailable.Code)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Extension outbox item {OutboxId} processing failed", work.Id);
                await Task.Delay(PollInterval, _timeProvider, cancellationToken);
            }
        }
    }

    private async Task<ExtensionOutboxWorkItem?> FindNextAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        TDbContext dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var pending = await dbContext.ReadNextOutboxAsync(_timeProvider.GetUtcNow(), cancellationToken);
        if (pending is null)
        {
            return null;
        }

        return new ExtensionOutboxWorkItem(
            pending.Id,
            pending.CreateBy ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid)
        );
    }

    private async ValueTask DispatchAsync(
        Guid outboxId,
        IExtensionContext extensionContext,
        CancellationToken cancellationToken
    )
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        TDbContext dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        ExtensionOutboxRow? pending = await dbContext
            .Set<ExtensionOutboxRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == outboxId, cancellationToken);
        if (
            pending is null
            || pending.State != ExtensionOutboxState.Pending
            || pending.NextAttemptAt > _timeProvider.GetUtcNow()
        )
            return;

        object? result = null;
        Exception? failure = null;
        try
        {
            result = await InvokeCapabilityAsync(pending, extensionContext, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await PersistResultAsync(
                scope.ServiceProvider,
                dbContext,
                pending,
                extensionContext,
                result,
                failure,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Extension outbox item {OutboxId} could not record its result", outboxId);
            await DeferResultFailureAsync(pending, exception, cancellationToken);
        }
    }

    private async Task PersistResultAsync(
        IServiceProvider services,
        TDbContext dbContext,
        ExtensionOutboxRow pending,
        IExtensionContext context,
        object? result,
        Exception? failure,
        CancellationToken cancellationToken
    )
    {
        var rejection =
            failure is AgwException { StatusCode: var status } agwException && (int)status is >= 400 and < 500
                ? agwException
                : null;
        var handler =
            failure is null || rejection is not null
                ? services.GetKeyedService<IExtensionOutboxResultHandler>(pending.Kind)
                : null;
        await using var lease = handler?.GetResultLeaseName(context) is { } leaseName
            ? await context.Leases.AcquireAsync(leaseName, cancellationToken)
            : null;
        using var leaseCancellation = lease is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.HandleLostToken);
        var token = leaseCancellation?.Token ?? cancellationToken;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
        var row = await dbContext.Set<ExtensionOutboxRow>().SingleOrDefaultAsync(item => item.Id == pending.Id, token);
        if (row is null || row.State != ExtensionOutboxState.Pending || row.Attempt != pending.Attempt)
        {
            await transaction.CommitAsync(token);
            return;
        }

        if (failure is not null && rejection is null)
        {
            ScheduleRetry(row, failure);
        }
        else
        {
            row.Attempt++;
            row.State = rejection is null ? ExtensionOutboxState.Completed : ExtensionOutboxState.Rejected;
            row.ResultJson = rejection is null ? JsonSerializer.Serialize(result, context.JsonOptions) : null;
            row.CompletedAt = _timeProvider.GetUtcNow();
            row.NextAttemptAt = null;
            row.LastError = rejection?.Message;
            row.ErrorCode = rejection?.Code;
            if (handler is not null)
            {
                await NotifyResultAsync(handler, row, token);
            }
        }

        await dbContext.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    private async Task DeferResultFailureAsync(
        ExtensionOutboxRow pending,
        Exception failure,
        CancellationToken cancellationToken
    )
    {
        // 回滚结果事务后，使用独立上下文保存退避时间。
        // Persist backoff in a fresh context after the result transaction rolls back.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var row = await dbContext
            .Set<ExtensionOutboxRow>()
            .SingleOrDefaultAsync(item => item.Id == pending.Id, cancellationToken);
        if (row is null || row.State != ExtensionOutboxState.Pending || row.Attempt != pending.Attempt)
            return;

        ScheduleRetry(row, failure);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void ScheduleRetry(ExtensionOutboxRow row, Exception failure)
    {
        row.Attempt++;
        row.LastError = failure.Message;
        row.NextAttemptAt = _timeProvider.GetUtcNow() + GetRetryDelay(row.Attempt);
    }

    private async ValueTask<object?> InvokeCapabilityAsync(
        ExtensionOutboxRow row,
        IExtensionContext extensionContext,
        CancellationToken cancellationToken
    )
    {
        IExtensionHostCapabilities capabilities = extensionContext.HostCapabilities;
        JsonSerializerOptions jsonOptions = extensionContext.JsonOptions;
        return row.Kind switch
        {
            ExtensionOutboxKind.PublishEvent => await capabilities
                .GetRequired<IEventPublisher>()
                .PublishAsync(Deserialize<CloudEventDraft>(row, jsonOptions), cancellationToken),
            ExtensionOutboxKind.UpsertSchedule => await capabilities
                .GetRequired<IEventScheduler>()
                .UpsertAsync(Deserialize<ScheduleRequest>(row, jsonOptions), cancellationToken),
            ExtensionOutboxKind.CancelSchedule => await InvokeCancelScheduleAsync(
                row,
                capabilities.GetRequired<IEventScheduler>(),
                cancellationToken
            ),
            ExtensionOutboxKind.UpsertManagedSubscription => await capabilities
                .GetRequired<IEventSubscriptionFacade>()
                .UpsertManagedAsync(Deserialize<ManagedSubscriptionRequest>(row, jsonOptions), cancellationToken),
            ExtensionOutboxKind.DeleteManagedSubscription => await InvokeDeleteManagedSubscriptionAsync(
                row,
                capabilities.GetRequired<IEventSubscriptionFacade>(),
                cancellationToken
            ),
            ExtensionOutboxKind.SubmitExecution => await capabilities
                .GetRequired<IBackgroundExecutionFacade>()
                .SubmitAsync(Deserialize<BackgroundExecutionRequest>(row, jsonOptions), cancellationToken),
            ExtensionOutboxKind.CancelExecution => await capabilities
                .GetRequired<IBackgroundExecutionFacade>()
                .CancelAsync(Deserialize<BackgroundExecutionCancelRequest>(row, jsonOptions), cancellationToken),
            ExtensionOutboxKind.Notify => await capabilities
                .GetRequired<INotificationFacade>()
                .CreateAsync(Deserialize<NotificationRequest>(row, jsonOptions), cancellationToken),
            ExtensionOutboxKind.UpdateNotification => await InvokeUpdateNotificationAsync(
                row,
                capabilities.GetRequired<INotificationFacade>(),
                jsonOptions,
                cancellationToken
            ),
            ExtensionOutboxKind.SubmitAnswer => await capabilities
                .GetRequired<INotificationAnswerFacade>()
                .SubmitAsync(Deserialize<NotificationAnswer>(row, jsonOptions), cancellationToken),
            _ => throw new AgwException(ErrorCodes.InvalidParam),
        };
    }

    private static async ValueTask<ScheduleState> InvokeCancelScheduleAsync(
        ExtensionOutboxRow row,
        IEventScheduler scheduler,
        CancellationToken cancellationToken
    )
    {
        ExtensionCancelScheduleRequest request = Deserialize<ExtensionCancelScheduleRequest>(
            row,
            ExtensionOutboxPayloadJsonContext.Default.Options
        );
        return await scheduler.CancelAsync(request.BusinessKey, request.RequestSeq, cancellationToken);
    }

    private static async ValueTask<ManagedSubscriptionState> InvokeDeleteManagedSubscriptionAsync(
        ExtensionOutboxRow row,
        IEventSubscriptionFacade facade,
        CancellationToken cancellationToken
    )
    {
        ExtensionDeleteManagedSubscriptionRequest request = Deserialize<ExtensionDeleteManagedSubscriptionRequest>(
            row,
            ExtensionOutboxPayloadJsonContext.Default.Options
        );
        return await facade.DeleteManagedAsync(request.ManagedKey, request.ManagedVersion, cancellationToken);
    }

    private static async ValueTask<object?> InvokeUpdateNotificationAsync(
        ExtensionOutboxRow row,
        INotificationFacade facade,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken
    )
    {
        await facade.UpdateAsync(Deserialize<NotificationUpdate>(row, jsonOptions), cancellationToken);
        return null;
    }

    private static TPayload Deserialize<TPayload>(ExtensionOutboxRow row, JsonSerializerOptions jsonOptions)
    {
        try
        {
            return JsonSerializer.Deserialize<TPayload>(row.PayloadJson, jsonOptions)
                ?? throw new AgwException(ErrorCodes.InvalidParam);
        }
        catch (JsonException exception)
        {
            throw new AgwException(ErrorCodes.InvalidParam, ErrorCodes.InvalidParam.Message, exception);
        }
    }

    private static TimeSpan GetRetryDelay(int attempt)
    {
        double multiplier = Math.Pow(2, Math.Min(attempt - 1, 10));
        return TimeSpan.FromMilliseconds(
            Math.Min(BaseRetryDelay.TotalMilliseconds * multiplier, MaximumRetryDelay.TotalMilliseconds)
        );
    }

    private static ValueTask NotifyResultAsync(
        IExtensionOutboxResultHandler handler,
        ExtensionOutboxRow row,
        CancellationToken cancellationToken
    ) =>
        handler.HandleAsync(
            new ExtensionOutboxResult
            {
                Id = row.Id,
                Kind = row.Kind,
                IdempotencyKey = row.IdempotencyKey,
                State = row.State,
                ResultJson = row.ResultJson,
                ErrorCode = row.ErrorCode,
                Error = row.LastError,
            },
            cancellationToken
        );

    private sealed record ExtensionOutboxWorkItem(Guid Id, string UserId);
}

internal sealed record ExtensionCancelScheduleRequest
{
    public required string BusinessKey { get; init; }

    public required long RequestSeq { get; init; }
}

internal sealed record ExtensionDeleteManagedSubscriptionRequest
{
    public required string ManagedKey { get; init; }

    public required long ManagedVersion { get; init; }
}
