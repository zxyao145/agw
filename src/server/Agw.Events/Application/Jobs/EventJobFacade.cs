using System.Text.Json;
using Agw.Agents.Contracts.Execution;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Application.Sources;
using Agw.Events.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Events.Application.Jobs;

public sealed class EventJobFacade : IEventJobFacade
{
    private const string FiredType = "job.timer.fired";
    private const int MaxNameLength = 200;
    private const int MaxRunLimit = 500;

    private readonly IEventsDbContext _db;
    private readonly EventTimerScheduleSync _timerSchedule;
    private readonly IEventIngestTransaction _transaction;
    private readonly IEventJobProjectLock _projectLock;
    private readonly IApplicationLock _applicationLock;
    private readonly TimeProvider _timeProvider;

    public EventJobFacade(
        IEventsDbContext db,
        EventTimerScheduleSync timerSchedule,
        IEventIngestTransaction transaction,
        IEventJobProjectLock projectLock,
        IApplicationLock applicationLock,
        TimeProvider timeProvider
    )
    {
        _db = db;
        _timerSchedule = timerSchedule;
        _transaction = transaction;
        _projectLock = projectLock;
        _applicationLock = applicationLock;
        _timeProvider = timeProvider;
    }

    public Task<EventJob> CreateAsync(EventJobDefinition definition, CancellationToken cancellationToken)
    {
        var owner = UserInfoUtil.RequiredUserId;
        var jobId = Guid.CreateVersion7();
        var sourceId = Guid.CreateVersion7();
        var source = new EventSource
        {
            Id = sourceId,
            CreateBy = owner,
            Kind = EventSourceKind.Timer,
            CeSource = $"/agw/sources/{sourceId:N}",
            ConfigJson = TimerConfig(jobId, definition),
            ManagedBy = EventManagedBy.Job,
            ManagedKey = JobKey(jobId),
            Version = 1,
        };
        var subscription = new EventSubscription
        {
            Id = Guid.CreateVersion7(),
            CreateBy = owner,
            ManagedBy = EventManagedBy.Job,
            ManagedKey = JobKey(jobId),
            SourceIdsJson = JsonSerializer.Serialize(new[] { sourceId }),
            EventTypesJson = JsonSerializer.Serialize(new[] { FiredType }),
            PermissionMode = (int)AgentExecutionPermissionMode.FullAccess,
            Version = 1,
        };
        var records = new JobRecords(source, subscription);
        ApplyDefinition(records, definition);

        return WriteAsync(
            async token =>
            {
                if (!await _projectLock.TryLockAsync(definition.ProjectId, token))
                    throw new AgwException(ErrorCodes.ResourceNotFound);

                _db.EventSources.Add(source);
                _db.EventSubscriptions.Add(subscription);
                if (source.Enabled)
                {
                    await _timerSchedule.SyncAsync(source, source.Version, token);
                }
                return await ReadJobAsync(records, token);
            },
            cancellationToken
        );
    }

    public Task<EventJob> UpdateAsync(
        Guid jobId,
        EventJobDefinition definition,
        CancellationToken cancellationToken,
        Guid? expectedProjectId = null
    ) =>
        ChangeAsync(
            jobId,
            async (records, token) =>
            {
                var source = records.Source;
                var config = TimerConfig(jobId, definition);
                var scheduleChanged = source.ConfigJson != config || source.Enabled != definition.Enabled;
                source.ConfigJson = config;
                ApplyDefinition(records, definition);
                AdvanceVersions(records);
                if (scheduleChanged)
                {
                    await _timerSchedule.SyncAsync(source, source.Version, token);
                }
                return await ReadJobAsync(records, token);
            },
            cancellationToken,
            destinationProjectId: definition.ProjectId,
            expectedProjectId: expectedProjectId
        );

    public Task<EventJob> SetEnabledAsync(Guid jobId, bool enabled, CancellationToken cancellationToken) =>
        ChangeAsync(
            jobId,
            async (records, token) =>
            {
                var source = records.Source;
                if (source.Enabled != enabled)
                {
                    source.Enabled = enabled;
                    records.Subscription.Enabled = enabled;
                    AdvanceVersions(records);
                    await _timerSchedule.SyncAsync(source, source.Version, token);
                }
                return await ReadJobAsync(records, token);
            },
            cancellationToken
        );

    public Task<EventJob> DeleteAsync(Guid jobId, CancellationToken cancellationToken) =>
        ChangeAsync(
            jobId,
            async (records, token) =>
            {
                var job = await ReadJobAsync(records, token);
                var source = records.Source;
                source.Enabled = false;
                source.NextRunAt = null;
                source.DeletedAt = _timeProvider.GetUtcNow();
                source.Version++;
                _db.EventSubscriptions.Remove(records.Subscription);
                await _timerSchedule.SyncAsync(source, source.Version, token);
                return job;
            },
            cancellationToken
        );

    public async Task<EventJob?> FindAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var records = await QueryJobs(jobId: jobId).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return records is null ? null : await ReadJobAsync(records, cancellationToken);
    }

    public async Task<IReadOnlyList<EventJob>> ListAsync(Guid? projectId, CancellationToken cancellationToken)
    {
        var records = await QueryJobs(projectId: projectId).AsNoTracking().ToArrayAsync(cancellationToken);
        var jobs = await ReadJobsAsync(records, cancellationToken);
        return jobs.OrderBy(job => job.DueAt is null)
            .ThenBy(job => job.DueAt)
            .ThenBy(job => job.Definition.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<EventJobRun>> ListRunsAsync(
        Guid jobId,
        int offset,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (offset < 0 || limit is < 1 or > MaxRunLimit)
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        var records =
            await QueryJobs(jobId: jobId).AsNoTracking().SingleOrDefaultAsync(cancellationToken)
            ?? throw new AgwException(ErrorCodes.JobNotFound);
        var attempts =
            from attempt in _db.EventDeliveryAttempts.AsNoTracking()
            join delivery in _db.EventDeliveries on attempt.DeliveryId equals delivery.Id
            where
                delivery.SubscriptionId == records.Subscription.Id
                && (
                    attempt.Attempt != delivery.Attempt
                    || delivery.State != EventDeliveryState.Running
                        && delivery.State != EventDeliveryState.WaitingExecution
                )
            orderby attempt.StartedAt descending, attempt.Id descending
            select new EventJobRun
            {
                Id = attempt.Id,
                DeliveryId = attempt.DeliveryId,
                Attempt = attempt.Attempt,
                StartedAt = attempt.StartedAt,
                EndedAt = attempt.EndedAt,
                Result = (int)attempt.Result,
                Error = attempt.Error,
                TurnId = attempt.TurnId,
            };
        return await attempts.Skip(offset).Take(limit).ToArrayAsync(cancellationToken);
    }

    public Task<int> CountAsync(CancellationToken cancellationToken)
    {
        var owner = UserInfoUtil.RequiredUserId;
        return _db.EventSources.CountAsync(
            source =>
                source.CreateBy == owner
                && source.Kind == EventSourceKind.Timer
                && source.ManagedBy == EventManagedBy.Job
                && source.DeletedAt == null,
            cancellationToken
        );
    }

    private async Task<EventJob> ChangeAsync(
        Guid jobId,
        Func<JobRecords, CancellationToken, Task<EventJob>> change,
        CancellationToken cancellationToken,
        Guid? destinationProjectId = null,
        Guid? expectedProjectId = null
    )
    {
        await using var lease = await _applicationLock.AcquireAsync($"events:job:{jobId:N}", cancellationToken);
        return await WriteAsync(
            async token =>
            {
                var records = await QueryJobs(jobId: jobId).SingleOrDefaultAsync(token);
                if (expectedProjectId.HasValue && records?.Subscription.ProjectId != expectedProjectId)
                    throw new AgwException(ErrorCodes.JobModificationConflict);
                if (records is null)
                    throw new AgwException(ErrorCodes.JobNotFound);
                await LockProjectsForChangeAsync(records.Subscription.ProjectId!.Value, destinationProjectId, token);
                var job = await change(records, token);
                lease.HandleLostToken.ThrowIfCancellationRequested();
                return job;
            },
            cancellationToken
        );
    }

    private async Task LockProjectsForChangeAsync(
        Guid currentProjectId,
        Guid? destinationProjectId,
        CancellationToken cancellationToken
    )
    {
        var destination = destinationProjectId ?? currentProjectId;
        var projectIds = new[] { currentProjectId, destination }.Distinct().Order();
        foreach (var projectId in projectIds)
        {
            if (await _projectLock.TryLockAsync(projectId, cancellationToken))
                continue;

            throw new AgwException(
                projectId == currentProjectId ? ErrorCodes.JobModificationConflict : ErrorCodes.ResourceNotFound
            );
        }
    }

    private async Task<EventJob> WriteAsync(
        Func<CancellationToken, Task<EventJob>> write,
        CancellationToken cancellationToken
    )
    {
        EventJob? result = null;
        try
        {
            await _transaction.ExecuteAsync(
                async token =>
                {
                    result = await write(token);
                },
                cancellationToken
            );
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new AgwException(
                ErrorCodes.JobModificationConflict,
                ErrorCodes.JobModificationConflict.Message,
                exception
            );
        }
        return result!;
    }

    private IQueryable<JobRecords> QueryJobs(Guid? jobId = null, Guid? projectId = null)
    {
        var owner = UserInfoUtil.RequiredUserId;
        var jobKey = jobId is { } id ? JobKey(id) : null;
        return from source in _db.EventSources
            join subscription in _db.EventSubscriptions on source.ManagedKey equals subscription.ManagedKey
            where
                source.CreateBy == owner
                && subscription.CreateBy == owner
                && source.Kind == EventSourceKind.Timer
                && source.ManagedBy == EventManagedBy.Job
                && subscription.ManagedBy == EventManagedBy.Job
                && source.DeletedAt == null
                && (jobKey == null || source.ManagedKey == jobKey)
                && (projectId == null || subscription.ProjectId == projectId)
            select new JobRecords(source, subscription);
    }

    private async Task<EventJob> ReadJobAsync(JobRecords records, CancellationToken cancellationToken) =>
        (await ReadJobsAsync([records], cancellationToken))[0];

    private async Task<EventJob[]> ReadJobsAsync(IReadOnlyList<JobRecords> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return [];
        }
        var keys = records.Select(record => $"source:{record.Source.Id}").ToArray();
        var schedules = await _db
            .EventSchedules.AsNoTracking()
            .Where(row => keys.Contains(row.CallerKey) && row.BusinessKey == row.CallerKey)
            .Select(row => new
            {
                row.CallerKey,
                row.State,
                row.DueAt,
            })
            .ToDictionaryAsync(row => row.CallerKey, cancellationToken);
        var subscriptionIds = records.Select(record => record.Subscription.Id).ToArray();
        var deliveries = await _db
            .EventDeliveries.AsNoTracking()
            .Where(row =>
                subscriptionIds.Contains(row.SubscriptionId)
                && row.Id
                    == _db.EventDeliveries.Where(candidate => candidate.SubscriptionId == row.SubscriptionId)
                        .OrderByDescending(candidate => candidate.Id)
                        .Select(candidate => candidate.Id)
                        .First()
            )
            .ToDictionaryAsync(
                row => row.SubscriptionId,
                row => new EventJobDelivery
                {
                    Id = row.Id,
                    State = (int)row.State,
                    Attempt = row.Attempt,
                    LastError = row.LastError,
                },
                cancellationToken
            );
        return records
            .Select(record =>
            {
                var schedule = schedules.GetValueOrDefault($"source:{record.Source.Id}");
                return new EventJob
                {
                    Id = Guid.Parse(record.Source.ManagedKey!),
                    Definition = ReadDefinition(record),
                    DueAt = schedule?.State == EventScheduleState.Active ? schedule.DueAt : null,
                    ScheduleCompleted = schedule?.State == EventScheduleState.Completed,
                    LastDelivery = deliveries.GetValueOrDefault(record.Subscription.Id),
                };
            })
            .ToArray();
    }

    private static EventJobDefinition ReadDefinition(JobRecords records)
    {
        using var document = JsonDocument.Parse(records.Source.ConfigJson);
        var timer = EventSourceConfigParser.ParseTimer(document.RootElement);
        return new EventJobDefinition
        {
            Name = records.Source.Name,
            Enabled = records.Source.Enabled,
            ProjectId = records.Subscription.ProjectId!.Value,
            TargetKind = (EventJobTargetKind)records.Subscription.TargetKind,
            AgentId = records.Subscription.AgentId!.Value,
            Instructions = records.Subscription.Instructions!,
            Schedule = new EventJobSchedule
            {
                Kind = timer.Kind,
                At = timer.At,
                Interval = timer.Interval,
                Cron = timer.Cron,
            },
        };
    }

    private static string TimerConfig(Guid jobId, EventJobDefinition definition)
    {
        var schedule = definition.Schedule ?? throw new AgwException(ErrorCodes.InvalidParam);
        var validName = !string.IsNullOrWhiteSpace(definition.Name) && definition.Name.Length <= MaxNameLength;
        var validTarget = Enum.IsDefined(definition.TargetKind);
        if (!validName || !validTarget || string.IsNullOrWhiteSpace(definition.Instructions))
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        var kind = schedule.Kind;
        const string timeZone = "UTC";
        var template = new { type = FiredType, subject = jobId.ToString("D") };
        var config = kind switch
        {
            "Once" => JsonSerializer.SerializeToElement(
                new
                {
                    kind,
                    at = schedule.At?.ToUniversalTime(),
                    timeZone,
                    @event = template,
                }
            ),
            "Interval" => JsonSerializer.SerializeToElement(
                new
                {
                    kind,
                    interval = schedule.Interval,
                    timeZone,
                    @event = template,
                }
            ),
            "Cron" => JsonSerializer.SerializeToElement(
                new
                {
                    kind,
                    cron = schedule.Cron,
                    timeZone,
                    @event = template,
                }
            ),
            _ => throw new AgwException(ErrorCodes.InvalidParam),
        };
        _ = EventSourceConfigParser.ParseTimer(config);
        return config.GetRawText();
    }

    private static void ApplyDefinition(JobRecords records, EventJobDefinition definition)
    {
        records.Source.Name = definition.Name;
        records.Source.Enabled = definition.Enabled;
        records.Subscription.Name = definition.Name;
        records.Subscription.Enabled = definition.Enabled;
        records.Subscription.ProjectId = definition.ProjectId;
        records.Subscription.TargetKind = (EventSubscriptionTargetKind)definition.TargetKind;
        records.Subscription.AgentId = definition.AgentId;
        records.Subscription.Instructions = definition.Instructions;
    }

    // Job 的 managed_key 是 Job ID 的 D 格式文本。
    // A Job's managed_key is the Job ID in D format.
    private static string JobKey(Guid jobId) => jobId.ToString("D");

    private static void AdvanceVersions(JobRecords records)
    {
        records.Source.Version++;
        records.Subscription.Version++;
    }

    private sealed record JobRecords(EventSource Source, EventSubscription Subscription);
}
