using Agw.Jobs.Contracts;
using Agw.Jobs.Entities;
using Agw.Tasks.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Agw.Jobs.Services;

public sealed class HangfireJobAppService : IHangfireJobAppService
{
    private const string DefinitionsSetKey = "agw:hangfire:definitions";
    private const string DefinitionHashPrefix = "agw:hangfire:definition:";

    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly JobStorage _jobStorage;
    private readonly ILogger<HangfireJobAppService> _logger;

    public HangfireJobAppService(
        IBackgroundJobClient backgroundJobClient,
        IRecurringJobManager recurringJobManager,
        JobStorage jobStorage,
        ILogger<HangfireJobAppService> logger)
    {
        _backgroundJobClient = backgroundJobClient;
        _recurringJobManager = recurringJobManager;
        _jobStorage = jobStorage;
        _logger = logger;
    }

    public async Task<IReadOnlyList<HangfireJobSummaryResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var definitions = await LoadDefinitionsAsync(cancellationToken);
        return definitions
            .OrderByDescending(x => x.UpdatedAtUtc ?? x.CreatedAtUtc)
            .Select(MapSummary)
            .ToList();
    }

    public async Task<HangfireJobDetailResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var definition = await LoadDefinitionAsync(id, cancellationToken);
        if (definition == null)
        {
            return null;
        }

        return MapDetail(definition);
    }

    public async Task<ApplicationResult<HangfireJobDetailResponse>> CreateAsync(
        HangfireJobUpsertRequest request,
        string user,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateRequest(request);
        if (validationError != null)
        {
            return ApplicationResult<HangfireJobDetailResponse>.Invalid(validationError);
        }

        var now = DateTimeOffset.UtcNow;
        var definition = new ManagedHangfireJobDefinition
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = NormalizeNullable(request.Description),
            JobType = request.JobType,
            Status = GetInitialStatus(request.JobType),
            Queue = NormalizeQueue(request.Queue),
            CronExpression = NormalizeNullable(request.CronExpression),
            DelaySeconds = request.DelaySeconds,
            Payload = NormalizeNullable(request.Payload),
            CreatedAtUtc = now,
            CreatedBy = user,
            UpdatedAtUtc = now,
            UpdatedBy = user
        };

        await PersistDefinitionAsync(definition, cancellationToken);
        await ScheduleJobAsync(definition, cancellationToken);
        await PersistDefinitionAsync(definition, cancellationToken);

        return ApplicationResult<HangfireJobDetailResponse>.Success(MapDetail(definition));
    }

    public async Task<ApplicationResult<HangfireJobDetailResponse>> UpdateAsync(
        Guid id,
        HangfireJobUpsertRequest request,
        string user,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateRequest(request);
        if (validationError != null)
        {
            return ApplicationResult<HangfireJobDetailResponse>.Invalid(validationError);
        }

        var definition = await LoadDefinitionAsync(id, cancellationToken);
        if (definition == null)
        {
            return ApplicationResult<HangfireJobDetailResponse>.NotFound();
        }

        await UnscheduleJobAsync(definition, cancellationToken);

        definition.Name = request.Name.Trim();
        definition.Description = NormalizeNullable(request.Description);
        definition.JobType = request.JobType;
        definition.Queue = NormalizeQueue(request.Queue);
        definition.CronExpression = NormalizeNullable(request.CronExpression);
        definition.DelaySeconds = request.DelaySeconds;
        definition.Payload = NormalizeNullable(request.Payload);
        definition.UpdatedAtUtc = DateTimeOffset.UtcNow;
        definition.UpdatedBy = user;
        definition.LastError = null;

        if (definition.Status != HangfireManagedJobStatus.Paused)
        {
            definition.Status = GetInitialStatus(request.JobType);
            await ScheduleJobAsync(definition, cancellationToken);
        }

        await PersistDefinitionAsync(definition, cancellationToken);
        return ApplicationResult<HangfireJobDetailResponse>.Success(MapDetail(definition));
    }

    public async Task<ApplicationResult<HangfireJobDetailResponse>> PauseAsync(
        Guid id,
        string user,
        CancellationToken cancellationToken = default)
    {
        var definition = await LoadDefinitionAsync(id, cancellationToken);
        if (definition == null)
        {
            return ApplicationResult<HangfireJobDetailResponse>.NotFound();
        }

        await UnscheduleJobAsync(definition, cancellationToken);
        definition.Status = HangfireManagedJobStatus.Paused;
        definition.UpdatedAtUtc = DateTimeOffset.UtcNow;
        definition.UpdatedBy = user;
        definition.NextExecutionUtc = null;
        await PersistDefinitionAsync(definition, cancellationToken);

        return ApplicationResult<HangfireJobDetailResponse>.Success(MapDetail(definition));
    }

    public async Task<ApplicationResult<HangfireJobDetailResponse>> StartAsync(
        Guid id,
        string user,
        CancellationToken cancellationToken = default)
    {
        var definition = await LoadDefinitionAsync(id, cancellationToken);
        if (definition == null)
        {
            return ApplicationResult<HangfireJobDetailResponse>.NotFound();
        }

        await ScheduleJobAsync(definition, cancellationToken);
        definition.Status = GetInitialStatus(definition.JobType);
        definition.UpdatedAtUtc = DateTimeOffset.UtcNow;
        definition.UpdatedBy = user;
        await PersistDefinitionAsync(definition, cancellationToken);

        return ApplicationResult<HangfireJobDetailResponse>.Success(MapDetail(definition));
    }

    public async Task<ApplicationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var definition = await LoadDefinitionAsync(id, cancellationToken);
        if (definition == null)
        {
            return ApplicationResult.NotFound();
        }

        await UnscheduleJobAsync(definition, cancellationToken);

        using var connection = _jobStorage.GetConnection();
        using var transaction = connection.CreateWriteTransaction();
        transaction.RemoveHash(GetDefinitionHashKey(id));
        transaction.RemoveFromSet(DefinitionsSetKey, id.ToString("N"));
        transaction.Commit();

        return ApplicationResult.Success();
    }

    private async Task<IReadOnlyList<ManagedHangfireJobDefinition>> LoadDefinitionsAsync(CancellationToken cancellationToken)
    {
        using var connection = _jobStorage.GetConnection();
        var ids = connection.GetAllItemsFromSet(DefinitionsSetKey) ?? [];
        var definitions = new List<ManagedHangfireJobDefinition>();
        foreach (var idValue in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(idValue, out var id))
            {
                continue;
            }

            var definition = await LoadDefinitionAsync(id, cancellationToken);
            if (definition != null)
            {
                definitions.Add(definition);
            }
        }

        return definitions;
    }

    private async Task<ManagedHangfireJobDefinition?> LoadDefinitionAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _jobStorage.GetConnection();
        var entries = connection.GetAllEntriesFromHash(GetDefinitionHashKey(id));
        if (entries == null || entries.Count == 0)
        {
            return null;
        }

        var definition = Deserialize(entries);
        return await EnrichRuntimeStateAsync(definition, cancellationToken);
    }

    private async Task<ManagedHangfireJobDefinition> EnrichRuntimeStateAsync(
        ManagedHangfireJobDefinition definition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var monitoringApi = _jobStorage.GetMonitoringApi();
        if (!string.IsNullOrWhiteSpace(definition.BackgroundJobId))
        {
            var jobDetails = monitoringApi.JobDetails(definition.BackgroundJobId);
            if (jobDetails != null)
            {
                definition.Status = MapStatus(definition.Status, jobDetails.History.FirstOrDefault()?.StateName);
                definition.LastError = ExtractLastError(jobDetails.History) ?? definition.LastError;
                definition.LastExecutionUtc ??= jobDetails.History
                    .Select(x => new DateTimeOffset(DateTime.SpecifyKind(x.CreatedAt, DateTimeKind.Utc)))
                    .OrderByDescending(x => x)
                    .FirstOrDefault();
            }
        }

        if (!string.IsNullOrWhiteSpace(definition.RecurringJobId))
        {
            using var connection = _jobStorage.GetConnection();
            var recurringJob = connection.GetRecurringJobs()
                .FirstOrDefault(x => string.Equals(x.Id, definition.RecurringJobId, StringComparison.Ordinal));
            if (recurringJob != null)
            {
                definition.BackgroundJobId = recurringJob.LastJobId ?? definition.BackgroundJobId;
                definition.LastExecutionUtc = recurringJob.LastExecution.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(recurringJob.LastExecution.Value, DateTimeKind.Utc))
                    : definition.LastExecutionUtc;
                definition.NextExecutionUtc = recurringJob.NextExecution.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(recurringJob.NextExecution.Value, DateTimeKind.Utc))
                    : definition.NextExecutionUtc;
                definition.LastError = recurringJob.Error ?? definition.LastError;
                if (definition.Status != HangfireManagedJobStatus.Paused)
                {
                    definition.Status = recurringJob.LastJobState switch
                    {
                        null when recurringJob.NextExecution.HasValue => HangfireManagedJobStatus.Scheduled,
                        not null => MapStatus(definition.Status, recurringJob.LastJobState),
                        null => definition.Status,
                    };
                }
            }
        }

        return definition;
    }

    private async Task PersistDefinitionAsync(ManagedHangfireJobDefinition definition, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _jobStorage.GetConnection();
        using var transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash(GetDefinitionHashKey(definition.Id), Serialize(definition));
        transaction.AddToSet(DefinitionsSetKey, definition.Id.ToString("N"));
        transaction.Commit();
        await Task.CompletedTask;
    }

    private async Task ScheduleJobAsync(ManagedHangfireJobDefinition definition, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (definition.JobType)
        {
            case HangfireManagedJobType.Recurring:
                definition.RecurringJobId ??= $"agw-managed:{definition.Id:N}";
                _recurringJobManager.AddOrUpdate(
                    definition.RecurringJobId,
                    Job.FromExpression<ManagedHangfireJobExecutor>(x => x.ExecuteAsync(definition.Id, null, CancellationToken.None)),
                    definition.CronExpression!,
                    TimeZoneInfo.Utc,
                    definition.Queue);
                definition.NextExecutionUtc = null;
                break;

            case HangfireManagedJobType.Delayed:
                definition.BackgroundJobId = _backgroundJobClient.Create(
                    Job.FromExpression<ManagedHangfireJobExecutor>(x => x.ExecuteAsync(definition.Id, null, CancellationToken.None)),
                    new ScheduledState(TimeSpan.FromSeconds(definition.DelaySeconds!.Value)));
                definition.NextExecutionUtc = DateTimeOffset.UtcNow.AddSeconds(definition.DelaySeconds.Value);
                break;

            case HangfireManagedJobType.FireAndForget:
                definition.BackgroundJobId = _backgroundJobClient.Enqueue<ManagedHangfireJobExecutor>(
                    x => x.ExecuteAsync(definition.Id, null, CancellationToken.None));
                definition.NextExecutionUtc = null;
                break;
        }

        await Task.CompletedTask;
    }

    private async Task UnscheduleJobAsync(ManagedHangfireJobDefinition definition, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(definition.RecurringJobId))
        {
            _recurringJobManager.RemoveIfExists(definition.RecurringJobId);
        }

        if (!string.IsNullOrWhiteSpace(definition.BackgroundJobId))
        {
            _backgroundJobClient.Delete(definition.BackgroundJobId);
        }

        definition.NextExecutionUtc = null;
        await Task.CompletedTask;
    }

    private static string? ValidateRequest(HangfireJobUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "Job name is required.";
        }

        return request.JobType switch
        {
            HangfireManagedJobType.Recurring when string.IsNullOrWhiteSpace(request.CronExpression) =>
                "CronExpression is required for recurring jobs.",
            HangfireManagedJobType.Delayed when !request.DelaySeconds.HasValue || request.DelaySeconds <= 0 =>
                "DelaySeconds must be greater than zero for delayed jobs.",
            _ => null
        };
    }

    private static Dictionary<string, string> Serialize(ManagedHangfireJobDefinition definition) =>
        new()
        {
            ["Id"] = definition.Id.ToString("D"),
            ["Name"] = definition.Name,
            ["Description"] = definition.Description ?? string.Empty,
            ["JobType"] = ((int)definition.JobType).ToString(CultureInfo.InvariantCulture),
            ["Status"] = ((int)definition.Status).ToString(CultureInfo.InvariantCulture),
            ["Queue"] = definition.Queue,
            ["CronExpression"] = definition.CronExpression ?? string.Empty,
            ["DelaySeconds"] = definition.DelaySeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["Payload"] = definition.Payload ?? string.Empty,
            ["RecurringJobId"] = definition.RecurringJobId ?? string.Empty,
            ["BackgroundJobId"] = definition.BackgroundJobId ?? string.Empty,
            ["CreatedAtUtc"] = definition.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["CreatedBy"] = definition.CreatedBy ?? string.Empty,
            ["UpdatedAtUtc"] = definition.UpdatedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            ["UpdatedBy"] = definition.UpdatedBy ?? string.Empty,
            ["LastExecutionUtc"] = definition.LastExecutionUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            ["NextExecutionUtc"] = definition.NextExecutionUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            ["LastError"] = definition.LastError ?? string.Empty
        };

    private static ManagedHangfireJobDefinition Deserialize(IReadOnlyDictionary<string, string> data)
    {
        return new ManagedHangfireJobDefinition
        {
            Id = Guid.Parse(data["Id"]),
            Name = data["Name"],
            Description = NormalizeNullable(data.GetValueOrDefault("Description")),
            JobType = (HangfireManagedJobType)int.Parse(data["JobType"], CultureInfo.InvariantCulture),
            Status = (HangfireManagedJobStatus)int.Parse(data["Status"], CultureInfo.InvariantCulture),
            Queue = data.GetValueOrDefault("Queue") ?? "default",
            CronExpression = NormalizeNullable(data.GetValueOrDefault("CronExpression")),
            DelaySeconds = int.TryParse(data.GetValueOrDefault("DelaySeconds"), out var delay) ? delay : null,
            Payload = NormalizeNullable(data.GetValueOrDefault("Payload")),
            RecurringJobId = NormalizeNullable(data.GetValueOrDefault("RecurringJobId")),
            BackgroundJobId = NormalizeNullable(data.GetValueOrDefault("BackgroundJobId")),
            CreatedAtUtc = DateTimeOffset.Parse(data["CreatedAtUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            CreatedBy = NormalizeNullable(data.GetValueOrDefault("CreatedBy")),
            UpdatedAtUtc = DateTimeOffset.TryParse(data.GetValueOrDefault("UpdatedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt) ? updatedAt : null,
            UpdatedBy = NormalizeNullable(data.GetValueOrDefault("UpdatedBy")),
            LastExecutionUtc = DateTimeOffset.TryParse(data.GetValueOrDefault("LastExecutionUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastExecution) ? lastExecution : null,
            NextExecutionUtc = DateTimeOffset.TryParse(data.GetValueOrDefault("NextExecutionUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var nextExecution) ? nextExecution : null,
            LastError = NormalizeNullable(data.GetValueOrDefault("LastError"))
        };
    }

    private HangfireJobSummaryResponse MapSummary(ManagedHangfireJobDefinition definition)
    {
        var details = MapDetail(definition);
        return new HangfireJobSummaryResponse(
            details.Id,
            details.Name,
            details.Description,
            details.JobType,
            details.Status,
            details.Queue,
            details.CronExpression,
            details.DelaySeconds,
            details.Payload,
            details.RecurringJobId,
            details.BackgroundJobId,
            details.HangfireState,
            details.CreatedAtUtc,
            details.UpdatedAtUtc,
            details.LastExecutionUtc,
            details.NextExecutionUtc,
            details.LastError);
    }

    private HangfireJobDetailResponse MapDetail(ManagedHangfireJobDefinition definition)
    {
        var history = GetStateHistory(definition.BackgroundJobId);
        var hangfireState = history.FirstOrDefault()?.StateName;
        return new HangfireJobDetailResponse(
            definition.Id,
            definition.Name,
            definition.Description,
            definition.JobType,
            definition.Status,
            definition.Queue,
            definition.CronExpression,
            definition.DelaySeconds,
            definition.Payload,
            definition.RecurringJobId,
            definition.BackgroundJobId,
            hangfireState,
            definition.CreatedAtUtc,
            definition.UpdatedAtUtc,
            definition.LastExecutionUtc,
            definition.NextExecutionUtc,
            definition.LastError,
            history);
    }

    private IReadOnlyList<HangfireJobStateHistoryResponse> GetStateHistory(string? backgroundJobId)
    {
        if (string.IsNullOrWhiteSpace(backgroundJobId))
        {
            return [];
        }

        JobDetailsDto? jobDetails = null;
        try
        {
            jobDetails = _jobStorage.GetMonitoringApi().JobDetails(backgroundJobId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Hangfire history for background job {BackgroundJobId}", backgroundJobId);
        }

        if (jobDetails?.History == null)
        {
            return [];
        }

        return jobDetails.History
            .Select(x => new HangfireJobStateHistoryResponse(
                x.StateName,
                new DateTimeOffset(DateTime.SpecifyKind(x.CreatedAt, DateTimeKind.Utc)),
                x.Reason,
                x.Data?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<string, string>()))
            .ToList();
    }

    private static HangfireManagedJobStatus GetInitialStatus(HangfireManagedJobType jobType) => jobType switch
    {
        HangfireManagedJobType.Recurring => HangfireManagedJobStatus.Scheduled,
        HangfireManagedJobType.Delayed => HangfireManagedJobStatus.Scheduled,
        _ => HangfireManagedJobStatus.Enqueued
    };

    private static HangfireManagedJobStatus MapStatus(HangfireManagedJobStatus currentStatus, string? stateName) =>
        stateName?.ToLowerInvariant() switch
        {
            null => currentStatus,
            "scheduled" => HangfireManagedJobStatus.Scheduled,
            "enqueued" => HangfireManagedJobStatus.Enqueued,
            "processing" => HangfireManagedJobStatus.Processing,
            "succeeded" => HangfireManagedJobStatus.Succeeded,
            "failed" => HangfireManagedJobStatus.Failed,
            "deleted" => currentStatus == HangfireManagedJobStatus.Paused ? HangfireManagedJobStatus.Paused : HangfireManagedJobStatus.Deleted,
            _ => currentStatus
        };

    private static string? ExtractLastError(IList<StateHistoryDto>? history)
    {
        return history?
            .Where(x => string.Equals(x.StateName, "Failed", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Reason ?? (x.Data != null && x.Data.TryGetValue("ExceptionMessage", out var exceptionMessage) ? exceptionMessage : null))
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    private static string NormalizeQueue(string? queue) => string.IsNullOrWhiteSpace(queue) ? "default" : queue.Trim();

    private static string? NormalizeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetDefinitionHashKey(Guid id) => $"{DefinitionHashPrefix}{id:N}";
}
