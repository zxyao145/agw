using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Events.Contracts;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Integrations.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using DeliveryStage = Agw.Extensions.TaskPilot.TaskPilotDeliveryStages;

namespace Agw.Extensions.TaskPilot;

internal sealed partial class TaskPilotDeliveryCoordinator
{
    private const int MaxBranchSyncAttempts = 3;
    private readonly TaskPilotGitHubClient _github;
    private readonly TaskPilotExecutionCoordinator _executions;
    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _time;

    public TaskPilotDeliveryCoordinator(
        TaskPilotGitHubClient github,
        TaskPilotExecutionCoordinator executions,
        IHttpClientFactory http,
        TimeProvider time
    )
    {
        _github = github;
        _executions = executions;
        _http = http;
        _time = time;
    }

    public async ValueTask<ExtensionEventHandlerResult> HandleRuleEventAsync(
        TaskPilotDbContext db,
        IExtensionContext context,
        TaskPilotRule rule,
        ExtensionEventDelivery input,
        CancellationToken ct
    )
    {
        var config = RuleConfig(rule);
        if (input.Event.Data is not { Kind: CloudEventDataKind.Json, Json: { } data })
            return ExtensionEventHandlerResult.Failed("GitHub delivery events require JSON data.");
        if (
            !data.TryGetProperty("repository", out var repository)
            || !string.Equals(
                repository.GetProperty("full_name").GetString(),
                config.Repository,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return ExtensionEventHandlerResult.Succeeded;
        var numbers = new HashSet<int>();
        if (data.TryGetProperty("pull_request", out var pull))
            numbers.Add(pull.GetProperty("number").GetInt32());
        foreach (string eventName in new[] { "check_run", "workflow_run" })
            if (data.TryGetProperty(eventName, out var body) && body.TryGetProperty("pull_requests", out var pulls))
                foreach (var item in pulls.EnumerateArray())
                    numbers.Add(item.GetProperty("number").GetInt32());
        if (numbers.Count == 0 && data.TryGetProperty("number", out var number))
            numbers.Add(number.GetInt32());
        foreach (int pr in numbers)
        {
            string key = $"{config.Repository}#{pr}";
            var matter = await db.Matters.SingleOrDefaultAsync(
                row => row.RootId == rule.Id && row.MatterKey == key,
                ct
            );
            if (matter is null)
            {
                matter = new TaskPilotMatter
                {
                    RootId = rule.Id,
                    MatterKey = key,
                    Title = key,
                    State = TaskPilotMatterState.Active,
                    ChainId = input.ChainId,
                    Version = 1,
                };
                db.Matters.Add(matter);
            }
            var delivery = await db.Deliveries.SingleOrDefaultAsync(
                row => row.RootId == matter.Id && row.PullRequestNumber == pr,
                ct
            );
            if (delivery is null)
            {
                delivery = new TaskPilotDelivery
                {
                    RootId = matter.Id,
                    Repository = config.Repository,
                    PullRequestNumber = pr,
                    Stage = DeliveryStage.Preflight,
                    Version = 1,
                };
                db.Deliveries.Add(delivery);
                await db.SaveChangesAsync(ct);
            }
            await AdvanceAsync(db, context, rule, matter, delivery, input, ct);
        }
        return ExtensionEventHandlerResult.Succeeded;
    }

    public async ValueTask<ExtensionEventHandlerResult> HandleTickAsync(
        TaskPilotDbContext db,
        IExtensionContext context,
        ExtensionEventDelivery input,
        CancellationToken ct
    )
    {
        if (
            input.Event.Data is not { Kind: CloudEventDataKind.Json, Json: { } data }
            || !data.TryGetProperty("deliveryId", out var id)
            || !id.TryGetGuid(out var deliveryId)
        )
            return ExtensionEventHandlerResult.Failed("The delivery tick has no delivery ID.");
        var delivery = await db.Deliveries.SingleOrDefaultAsync(row => row.Id == deliveryId, ct);
        if (delivery is null || delivery.Stage is DeliveryStage.Done or DeliveryStage.ReviewOnlyDone)
            return ExtensionEventHandlerResult.Succeeded;
        if (
            !data.TryGetProperty("version", out var version)
            || !version.TryGetInt64(out var sequence)
            || sequence != delivery.Version
        )
            return ExtensionEventHandlerResult.Succeeded;
        var matter = await db.Matters.SingleAsync(row => row.Id == delivery.RootId, ct);
        var rule = await db.Rules.SingleAsync(row => row.Id == matter.RootId, ct);
        if (rule.DeletedAt is not null || !rule.Enabled)
            return ExtensionEventHandlerResult.Succeeded;
        await AdvanceAsync(db, context, rule, matter, delivery, input, ct);
        return ExtensionEventHandlerResult.Succeeded;
    }

    private async Task AdvanceAsync(
        TaskPilotDbContext db,
        IExtensionContext context,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotDelivery delivery,
        ExtensionEventDelivery input,
        CancellationToken ct
    )
    {
        Guid parentEventId = input.EventId;
        var settings = RuleConfig(rule);
        if (rule.Paused || matter.State is TaskPilotMatterState.Paused or TaskPilotMatterState.Stopped)
        {
            Schedule(db, context, delivery, settings, parentEventId);
            return;
        }
        try
        {
            string authorization = await context
                .HostCapabilities.GetRequired<IConnectionAuthorizationFacade>()
                .GetAuthorizationHeaderAsync(settings.ConnectionId, ct);
            var pull = await ReadPullAsync(delivery, authorization, ct);
            var progress = await PreparePullAsync(db, delivery, pull, ct);
            if (progress == AdvanceProgress.Continue)
            {
                var config = await ReadProjectConfigAsync(delivery, pull, ct);
                progress = await AdvanceReviewAsync(db, context, rule, matter, delivery, input, pull, config, ct);
                if (progress == AdvanceProgress.Continue)
                    progress = await AdvanceMergeAsync(
                        db,
                        context,
                        rule,
                        matter,
                        delivery,
                        parentEventId,
                        pull,
                        config,
                        ct
                    );
                if (progress == AdvanceProgress.Continue)
                    progress = await AdvanceShipAsync(db, delivery, pull, config, ct);
            }
            if (progress == AdvanceProgress.Wait)
                Schedule(db, context, delivery, settings, parentEventId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            delivery.LastError = exception.Message;
            if (
                delivery.Stage == DeliveryStage.Review
                && delivery.ReviewedSha is not null
                && delivery.FindingsJson is not null
            )
                Record(db, delivery, DeliveryStage.Review, "StatusPending", exception.Message);
            else
                Transition(db, delivery, DeliveryStage.Failed, "Failed", exception.Message);
            StageNotice(db, context, rule, delivery, parentEventId, "Delivery requires attention", exception.Message);
            Schedule(db, context, delivery, settings, parentEventId);
        }
    }

    private async Task<DeliveryPull> ReadPullAsync(
        TaskPilotDelivery delivery,
        string authorization,
        CancellationToken ct
    )
    {
        var pull = await _github.GetPullAsync(delivery.Repository, delivery.PullRequestNumber, authorization, ct);
        string head = pull.GetProperty("head").GetProperty("sha").GetString()!;
        string branch = pull.GetProperty("base").GetProperty("ref").GetString()!;
        string baseSha = await _github.GetBranchShaAsync(delivery.Repository, branch, authorization, ct);
        return new DeliveryPull
        {
            Payload = pull,
            Authorization = authorization,
            Head = head,
            Branch = branch,
            BaseSha = baseSha,
            Labels = Labels(pull),
        };
    }

    private async Task<AdvanceProgress> PreparePullAsync(
        TaskPilotDbContext db,
        TaskPilotDelivery delivery,
        DeliveryPull pull,
        CancellationToken ct
    )
    {
        if (delivery.HeadSha != pull.Head)
        {
            if (delivery.ReviewRoundId is { } activeRound)
            {
                var round = await db.Rounds.SingleAsync(row => row.Id == activeRound, ct);
                if (
                    round.State
                    is TaskPilotRoundState.Submitting
                        or TaskPilotRoundState.Running
                        or TaskPilotRoundState.Cancelling
                )
                    return AdvanceProgress.Wait;
            }
            delivery.HeadSha = pull.Head;
            delivery.ReviewedSha = null;
            delivery.ReviewRoundId = null;
            if (delivery.Stage != DeliveryStage.Sync)
                delivery.SyncAttempts = 0;
            Transition(db, delivery, DeliveryStage.Preflight, "HeadChanged");
        }

        bool merged = pull.Payload.GetProperty("merged").GetBoolean();
        bool hasDeliveryLabel = pull.Labels.Contains("deliver") || pull.Labels.Contains("deliver-review");
        if (!merged && !hasDeliveryLabel)
        {
            Transition(db, delivery, DeliveryStage.Done, "NoDeliveryLabel");
            return AdvanceProgress.Stop;
        }
        var currentMode = pull.Labels.Contains("deliver-review")
            ? TaskPilotDeliveryMode.ReviewOnly
            : TaskPilotDeliveryMode.Full;
        if (!merged && currentMode != delivery.Mode)
        {
            if (
                delivery.ReviewRoundId is { } roundId
                && await db.Rounds.AnyAsync(
                    row =>
                        row.Id == roundId
                        && (row.State == TaskPilotRoundState.Submitting || row.State == TaskPilotRoundState.Running),
                    ct
                )
            )
                return AdvanceProgress.Wait;

            delivery.Mode = currentMode;
            delivery.ReviewRoundId = null;
            delivery.ReviewedSha = null;
            Transition(db, delivery, DeliveryStage.Preflight, "ModeChanged");
        }
        if (merged)
        {
            delivery.MergeSha = pull.Payload.GetProperty("merge_commit_sha").GetString();
            if (
                delivery.Stage
                is not (
                    DeliveryStage.ShipWait
                    or DeliveryStage.HealthVerify
                    or DeliveryStage.Finalize
                    or DeliveryStage.Done
                )
            )
                Transition(db, delivery, DeliveryStage.ShipWait, "Merged");
        }
        else if (delivery.Stage is DeliveryStage.Done or DeliveryStage.ReviewOnlyDone or DeliveryStage.Failed)
            Transition(db, delivery, DeliveryStage.Preflight, "Recheck");

        if (delivery.Stage == DeliveryStage.FindingsReported && delivery.ReviewedSha == pull.Head)
            return AdvanceProgress.Stop;
        if (delivery.Stage == DeliveryStage.Preflight)
        {
            if (
                pull.Payload.GetProperty("state").GetString() != "open"
                || pull.Payload.GetProperty("draft").GetBoolean()
            )
                throw new AgwException(ErrorCodes.InvalidParam, "The pull request is closed or draft.");
            await _github.DisableAutoMergeAsync(delivery.Repository, pull.Payload, pull.Authorization, ct);
            delivery.Mode = currentMode;
            Transition(db, delivery, DeliveryStage.ReadConfig, "Ready");
        }
        return AdvanceProgress.Continue;
    }

    private async Task<TaskPilotDeliveryProjectConfig> ReadProjectConfigAsync(
        TaskPilotDelivery delivery,
        DeliveryPull pull,
        CancellationToken ct
    )
    {
        var config = TaskPilotDeliveryProjectConfig.Parse(
            await _github.ReadFileAsync(
                delivery.Repository,
                ".agw/delivery.json",
                pull.BaseSha,
                pull.Authorization,
                ct
            ),
            delivery.Repository
        );
        if (config.Branch != pull.Branch)
            throw new AgwException(ErrorCodes.InvalidParam, "The pull request targets a different delivery branch.");
        return config;
    }

    private async Task<AdvanceProgress> AdvanceReviewAsync(
        TaskPilotDbContext db,
        IExtensionContext context,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotDelivery delivery,
        ExtensionEventDelivery input,
        DeliveryPull pull,
        TaskPilotDeliveryProjectConfig config,
        CancellationToken ct
    )
    {
        if (delivery.Stage == DeliveryStage.ReadConfig)
            Transition(db, delivery, DeliveryStage.Sync, "ConfigurationRead");
        if (delivery.Stage == DeliveryStage.Sync)
        {
            if (!await _github.ContainsAsync(delivery.Repository, pull.BaseSha, pull.Head, pull.Authorization, ct))
            {
                if (delivery.SyncAttempts >= MaxBranchSyncAttempts)
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        "The pull request branch did not synchronize after three requests."
                    );
                await _github.UpdateBranchAsync(
                    delivery.Repository,
                    delivery.PullRequestNumber,
                    pull.Head,
                    pull.Authorization,
                    ct
                );
                delivery.SyncAttempts++;
                Record(db, delivery, DeliveryStage.Sync, "UpdateRequested");
                return AdvanceProgress.Wait;
            }
            delivery.BaseSha = pull.BaseSha;
            delivery.ReviewRoundId = null;
            delivery.ReviewedSha = null;
            Transition(db, delivery, DeliveryStage.Snapshot, "Synchronized");
        }
        if (delivery.Stage == DeliveryStage.Snapshot)
        {
            await SaveSnapshotAsync(context, delivery, pull, ct);
            Transition(db, delivery, DeliveryStage.Review, "SnapshotSaved");
        }
        if (delivery.Stage != DeliveryStage.Review)
            return AdvanceProgress.Continue;
        if (delivery.ReviewedSha != pull.Head || delivery.FindingsJson is not { } findingsJson)
        {
            await SubmitReviewAsync(db, context, rule, matter, delivery, input, ct);
            return AdvanceProgress.Wait;
        }

        var reviewResult =
            JsonSerializer.Deserialize(findingsJson, TaskPilotDeliveryJsonContext.Default.TaskPilotReviewResult)
            ?? throw new AgwException(ErrorCodes.InvalidParam);
        await _github.SetStatusAsync(
            delivery.Repository,
            pull.Head,
            config.Review.Status,
            reviewResult.Approved ? "success" : "failure",
            reviewResult.Summary,
            pull.Authorization,
            ct
        );
        if (!reviewResult.Approved)
        {
            Transition(db, delivery, DeliveryStage.FindingsReported, "FindingsReported");
            return AdvanceProgress.Stop;
        }

        var approvedStage =
            delivery.Mode == TaskPilotDeliveryMode.ReviewOnly ? DeliveryStage.Finalize : DeliveryStage.CheckWait;
        Transition(db, delivery, approvedStage, "Approved");
        return AdvanceProgress.Continue;
    }

    private async Task SaveSnapshotAsync(
        IExtensionContext context,
        TaskPilotDelivery delivery,
        DeliveryPull pull,
        CancellationToken ct
    )
    {
        string snapshot = await _github.CreateSnapshotAsync(
            delivery.Repository,
            pull.Payload,
            pull.BaseSha,
            pull.Head,
            pull.Authorization,
            ct
        );
        string path = SnapshotPath(context, delivery);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            return;

        string pendingPath = path + "." + Guid.CreateVersion7().ToString("N") + ".partial";
        try
        {
            await File.WriteAllTextAsync(pendingPath, snapshot, new UTF8Encoding(false), ct);
            File.SetAttributes(pendingPath, File.GetAttributes(pendingPath) | FileAttributes.ReadOnly);
            File.Move(pendingPath, path);
        }
        finally
        {
            if (File.Exists(pendingPath))
            {
                File.SetAttributes(pendingPath, FileAttributes.Normal);
                File.Delete(pendingPath);
            }
        }
    }

    private async Task<AdvanceProgress> AdvanceMergeAsync(
        TaskPilotDbContext db,
        IExtensionContext context,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotDelivery delivery,
        Guid parentEventId,
        DeliveryPull pull,
        TaskPilotDeliveryProjectConfig config,
        CancellationToken ct
    )
    {
        if (delivery.Stage == DeliveryStage.CheckWait)
        {
            AdvanceProgress checkProgress = await AdvanceChecksAsync(db, delivery, pull, config, ct);
            if (checkProgress == AdvanceProgress.Wait)
                return AdvanceProgress.Wait;
        }
        if (delivery.Stage == DeliveryStage.PreMergeVerify)
        {
            AdvanceProgress verifyProgress = await AdvancePreMergeVerificationAsync(
                db,
                context,
                rule,
                matter,
                delivery,
                parentEventId,
                pull,
                ct
            );
            if (verifyProgress == AdvanceProgress.Wait)
                return AdvanceProgress.Wait;
        }
        if (delivery.Stage != DeliveryStage.Merge)
            return AdvanceProgress.Continue;

        return await MergePullRequestAsync(db, delivery, pull, config, ct);
    }

    private async Task<AdvanceProgress> AdvanceChecksAsync(
        TaskPilotDbContext db,
        TaskPilotDelivery delivery,
        DeliveryPull pull,
        TaskPilotDeliveryProjectConfig config,
        CancellationToken ct
    )
    {
        var run = await _github.FindWorkflowAsync(
            delivery.Repository,
            config.Check.Workflow,
            pull.Payload.GetProperty("head").GetProperty("ref").GetString()!,
            pull.Authorization,
            item => Task.FromResult(IsWorkflowForReviewedPull(item, delivery)),
            ct
        );
        if (run is null || run.Value.GetProperty("status").GetString() != "completed")
            return AdvanceProgress.Wait;
        delivery.CheckRunId = run.Value.GetProperty("id").GetInt64();
        string? conclusion = await _github.GetJobConclusionAsync(
            delivery.Repository,
            delivery.CheckRunId.Value,
            config.Check.Job,
            pull.Authorization,
            ct
        );
        if (conclusion is null)
            return AdvanceProgress.Wait;
        if (conclusion != "success")
            throw new AgwException(ErrorCodes.InvalidParam, "The delivery check did not succeed.");

        Transition(db, delivery, DeliveryStage.PreMergeVerify, "ChecksPassed");
        return AdvanceProgress.Continue;
    }

    private static bool IsWorkflowForReviewedPull(JsonElement item, TaskPilotDelivery delivery) =>
        item.GetProperty("head_sha").GetString() == delivery.ReviewedSha
        && item.GetProperty("event").GetString() is "push" or "pull_request"
        && (
            item.GetProperty("event").GetString() == "push"
            || item.GetProperty("pull_requests")
                .EnumerateArray()
                .Any(pr => pr.GetProperty("number").GetInt32() == delivery.PullRequestNumber)
        );

    private async Task<AdvanceProgress> AdvancePreMergeVerificationAsync(
        TaskPilotDbContext db,
        IExtensionContext context,
        TaskPilotRule rule,
        TaskPilotMatter matter,
        TaskPilotDelivery delivery,
        Guid parentEventId,
        DeliveryPull pull,
        CancellationToken ct
    )
    {
        string nextStage = await GetPreMergeStageAsync(delivery, pull, ct);
        if (nextStage == DeliveryStage.Preflight)
        {
            Transition(db, delivery, DeliveryStage.Preflight, "PullRequestChanged");
            return AdvanceProgress.Wait;
        }
        if (nextStage == DeliveryStage.Sync)
        {
            delivery.ReviewRoundId = null;
            delivery.ReviewedSha = null;
            Transition(db, delivery, DeliveryStage.Sync, "TargetBranchAdvanced");
            return AdvanceProgress.Wait;
        }
        if (pull.Labels.Contains("deliver-attended") && !await IsAttendedApprovedAsync(db, delivery, ct))
        {
            await AskAttendedAsync(db, context, rule, matter, delivery, parentEventId, ct);
            return AdvanceProgress.Wait;
        }

        Transition(db, delivery, DeliveryStage.Merge, "Verified");
        return AdvanceProgress.Continue;
    }

    private async Task<string> GetPreMergeStageAsync(
        TaskPilotDelivery delivery,
        DeliveryPull pull,
        CancellationToken ct
    )
    {
        var current = await _github.GetPullAsync(
            delivery.Repository,
            delivery.PullRequestNumber,
            pull.Authorization,
            ct
        );
        string currentBase = await _github.GetBranchShaAsync(delivery.Repository, pull.Branch, pull.Authorization, ct);
        return TaskPilotDeliveryPolicy.GetPreMergeStage(
            current.GetProperty("head").GetProperty("sha").GetString()!,
            delivery.ReviewedSha!,
            currentBase,
            delivery.BaseSha!,
            pull.Labels,
            Labels(current)
        );
    }

    private async Task<AdvanceProgress> MergePullRequestAsync(
        TaskPilotDbContext db,
        TaskPilotDelivery delivery,
        DeliveryPull pull,
        TaskPilotDeliveryProjectConfig config,
        CancellationToken ct
    )
    {
        await _github.SetStatusAsync(
            delivery.Repository,
            delivery.ReviewedSha!,
            config.Review.Status,
            "success",
            "TaskPilot review approved",
            pull.Authorization,
            ct
        );
        if (
            !await _github.MergeAsync(
                delivery.Repository,
                delivery.PullRequestNumber,
                delivery.ReviewedSha!,
                pull.Authorization,
                ct
            )
        )
        {
            Transition(db, delivery, DeliveryStage.Preflight, "HeadChanged");
            return AdvanceProgress.Wait;
        }
        var merged = await _github.GetPullAsync(
            delivery.Repository,
            delivery.PullRequestNumber,
            pull.Authorization,
            ct
        );
        if (!merged.GetProperty("merged").GetBoolean())
            throw new AgwException(ErrorCodes.InvalidParam, "GitHub did not confirm the merge.");
        delivery.MergeSha = merged.GetProperty("merge_commit_sha").GetString();
        Transition(db, delivery, DeliveryStage.ShipWait, "Merged");
        return AdvanceProgress.Continue;
    }

    private async Task<AdvanceProgress> AdvanceShipAsync(
        TaskPilotDbContext db,
        TaskPilotDelivery delivery,
        DeliveryPull pull,
        TaskPilotDeliveryProjectConfig config,
        CancellationToken ct
    )
    {
        if (delivery.Stage == DeliveryStage.ShipWait)
        {
            var run = await _github.FindWorkflowAsync(
                delivery.Repository,
                config.Ship.Workflow,
                pull.Branch,
                pull.Authorization,
                async item =>
                    item.GetProperty("event").GetString() == "push"
                    && await _github.ContainsAsync(
                        delivery.Repository,
                        delivery.MergeSha!,
                        item.GetProperty("head_sha").GetString()!,
                        pull.Authorization,
                        ct
                    ),
                ct
            );
            if (run is null || run.Value.GetProperty("status").GetString() != "completed")
                return AdvanceProgress.Wait;
            delivery.ShipRunId = run.Value.GetProperty("id").GetInt64();
            if (run.Value.GetProperty("conclusion").GetString() != "success")
                throw new AgwException(ErrorCodes.InvalidParam, "The delivery shipping workflow did not succeed.");
            Transition(db, delivery, DeliveryStage.HealthVerify, "Shipped");
        }
        if (delivery.Stage == DeliveryStage.HealthVerify)
        {
            if (!await VerifyHealthAsync(delivery, config, pull.Authorization, ct))
                return AdvanceProgress.Wait;
            Transition(db, delivery, DeliveryStage.Finalize, "Healthy");
        }
        if (delivery.Stage == DeliveryStage.Finalize)
            await FinalizeAsync(db, delivery, config, pull.Authorization, ct);
        return delivery.Stage is DeliveryStage.Done or DeliveryStage.ReviewOnlyDone or DeliveryStage.FindingsReported
            ? AdvanceProgress.Stop
            : AdvanceProgress.Wait;
    }

    private enum AdvanceProgress
    {
        Continue,
        Wait,
        Stop,
    }

    private sealed record DeliveryPull
    {
        public required JsonElement Payload { get; init; }
        public required string Authorization { get; init; }
        public required string Head { get; init; }
        public required string Branch { get; init; }
        public required string BaseSha { get; init; }
        public required IReadOnlySet<string> Labels { get; init; }
    }

    private static TaskPilotDeliveryRuleConfig RuleConfig(TaskPilotRule rule) =>
        TaskPilotDeliveryRuleConfig.Parse(rule.DeliveryJson);

    private static HashSet<string> Labels(JsonElement pull) =>
        pull.GetProperty("labels")
            .EnumerateArray()
            .Select(label => label.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

    private void Transition(
        TaskPilotDbContext db,
        TaskPilotDelivery delivery,
        string stage,
        string result,
        string? detail = null
    )
    {
        delivery.Stage = stage;
        delivery.Version++;
        if (stage != DeliveryStage.Failed)
            delivery.LastError = null;
        Record(db, delivery, stage, result, detail);
    }

    private void Record(
        TaskPilotDbContext db,
        TaskPilotDelivery delivery,
        string stage,
        string result,
        string? detail = null
    ) =>
        db.DeliveryStages.Add(
            new TaskPilotDeliveryStage
            {
                RootId = delivery.Id,
                Stage = stage,
                Result = result,
                DetailJson = new JsonObject
                {
                    ["detail"] = detail,
                    ["headSha"] = delivery.HeadSha,
                    ["baseSha"] = delivery.BaseSha,
                    ["reviewedSha"] = delivery.ReviewedSha,
                    ["mergeSha"] = delivery.MergeSha,
                    ["checkRunId"] = delivery.CheckRunId,
                    ["shipRunId"] = delivery.ShipRunId,
                    ["liveSha"] = delivery.LiveSha,
                }.ToJsonString(),
                At = _time.GetUtcNow(),
            }
        );

    private void Schedule(
        TaskPilotDbContext db,
        IExtensionContext context,
        TaskPilotDelivery delivery,
        TaskPilotDeliveryRuleConfig config,
        Guid parentEventId
    )
    {
        delivery.Version++;
        var data = JsonSerializer.SerializeToElement(
            new JsonObject { ["deliveryId"] = delivery.Id.ToString("D"), ["version"] = delivery.Version }
        );
        var request = new ScheduleRequest
        {
            BusinessKey = $"delivery:{delivery.Id:D}:tick",
            RequestSeq = delivery.Version,
            Kind = ScheduleKind.Once,
            At = _time.GetUtcNow().AddSeconds(config.CheckIntervalSeconds),
            TimeZone = "UTC",
            ParentEventId = parentEventId,
            Event = new CloudEventDraft
            {
                Id = "template",
                Type = "taskpilot.delivery.tick",
                Subject = delivery.Id.ToString("D"),
                DataContentType = "application/json",
                Data = new CloudEventData
                {
                    Kind = CloudEventDataKind.Json,
                    Text = data.GetRawText(),
                    Json = data,
                },
            },
        };
        ExtensionOutbox.Enqueue(
            db,
            ExtensionOutboxKind.UpsertSchedule,
            $"delivery:{delivery.Id:D}:tick:{delivery.Version}",
            request,
            context.JsonOptions
        );
    }

    private static string SnapshotPath(IExtensionContext context, TaskPilotDelivery delivery)
    {
        string sha = delivery.HeadSha ?? throw new AgwException(ErrorCodes.InvalidParam);
        if (sha.Length is not (40 or 64) || !sha.All(char.IsAsciiHexDigit))
            throw new AgwException(ErrorCodes.InvalidParam);
        string baseSha = delivery.BaseSha ?? throw new AgwException(ErrorCodes.InvalidParam);
        if (baseSha.Length is not (40 or 64) || !baseSha.All(char.IsAsciiHexDigit))
            throw new AgwException(ErrorCodes.InvalidParam);
        return Path.Combine(
            context.DataDirectory,
            "delivery-snapshots",
            delivery.Id.ToString("N"),
            sha + "-" + baseSha + ".txt"
        );
    }
}
