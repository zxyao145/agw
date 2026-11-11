using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Agw.Agents;
using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Contracts.Execution;
using Agw.Agents.Execution;
using Agw.Auth.Contracts;
using Agw.Auth.Extensions;
using Agw.Events;
using Agw.Events.Contracts;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk.Database;
using Agw.Extensions.TestSupport;
using Agw.Files;
using Agw.Host.Data;
using Agw.Infrastructure;
using Agw.Infrastructure.Data;
using Agw.Integrations.Contracts;
using Agw.Integrations.Extensions;
using Agw.Notifications;
using Agw.Providers;
using Agw.Setup.Services;
using Agw.Shared.Configuration;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Skills;
using Agw.Tests;
using Agw.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Agw.Extensions.TaskPilot.Tests;

public sealed class TaskPilotRuntimeBehaviorTests
{
    private static readonly Guid ExtensionId = Guid.Parse("019a4c17-5f00-7a80-8b3d-2b732f48a101");
    private static readonly Guid AgentId = Guid.Parse("019a4c17-5f00-7a80-8b3d-2b732f48a201");
    private static readonly Guid ProjectId = Guid.Parse("019a4c17-5f00-7a80-8b3d-2b732f48a202");
    private static readonly Guid SourceId = Guid.Parse("019a4c17-5f00-7a80-8b3d-2b732f48a203");
    private static readonly Guid ExecutionSourceId = Guid.CreateVersion7();
    private static readonly Guid NotificationSourceId = Guid.CreateVersion7();
    private static readonly Guid SchedulerSourceId = Guid.CreateVersion7();
    private const string UserId = "taskpilot-runtime-test";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool PostgresConfigured => IsolatedTestConnections.Postgres is not null;

    [Fact]
    public async Task RuleEvent_QueuesRoundsAndKeepsTheConcurrencySlotOccupied()
    {
        (ExtensionRuntimeIntegrationHost host, ExtensionGenerationSnapshot generation) = await CreateHostAsync();
        await using (host)
        {
            Guid ruleId;
            await using (TaskPilotDbContext db = CreateDbContext(host, generation))
            {
                TaskPilotRule rule = NewRule();
                ruleId = rule.Id;
                db.Rules.Add(rule);
                db.RuleRevisions.Add(
                    new TaskPilotRuleRevision
                    {
                        RootId = rule.Id,
                        Version = rule.Version,
                        SnapshotJson = JsonSerializer.Serialize(ToRuleDetails(rule), JsonOptions),
                    }
                );
                db.UserStates.Add(NewUserState());
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await host.InvokeEventHandlerAsync(
                ExtensionId,
                RuleEventInvocation(ruleId, NewRuleEvent("matter-one")),
                TestContext.Current.CancellationToken
            );
            await host.InvokeEventHandlerAsync(
                ExtensionId,
                RuleEventInvocation(ruleId, NewRuleEvent("matter-one")),
                TestContext.Current.CancellationToken
            );
            await host.InvokeEventHandlerAsync(
                ExtensionId,
                RuleEventInvocation(ruleId, NewRuleEvent("matter-two")),
                TestContext.Current.CancellationToken
            );

            await using TaskPilotDbContext assertions = CreateDbContext(host, generation);
            Assert.Equal(3, await assertions.Rounds.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                2,
                await assertions.Rounds.CountAsync(
                    round => round.State == TaskPilotRoundState.Queued,
                    TestContext.Current.CancellationToken
                )
            );
            Assert.Equal(1, await assertions.MatterActivities.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await assertions.ConcurrencySlots.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await CountOutboxAsync(host, "SubmitExecution"));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task RuleEvent_WithScopeAndTags_UsesThoseFieldsInMatterKey(int hops)
    {
        var (host, generation) = await CreateHostAsync();
        await using (host)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var rule = NewRule(paused: true);
            rule.MatterKeyJson = """{"cat":[{"var":"scope"},":",{"var":"tags.0"}]}""";
            await using (var db = CreateDbContext(host, generation))
            {
                db.Rules.Add(rule);
                db.UserStates.Add(NewUserState());
                await db.SaveChangesAsync(cancellationToken);
            }

            var cloudEvent = NewRuleEvent("ignored-subject") with
            {
                Extensions = new Dictionary<string, CloudEventAttributeValue>
                {
                    ["scope"] = new(CloudEventAttributeKind.String, "release"),
                    ["tags"] = new(CloudEventAttributeKind.String, "backend,urgent"),
                },
            };
            var invocation = RuleEventInvocation(rule.Id, cloudEvent);
            var result = await host.InvokeEventHandlerAsync(
                ExtensionId,
                invocation with
                {
                    Delivery = invocation.Delivery with { Hops = hops },
                },
                cancellationToken
            );
            Assert.Equal(ExtensionEventHandlerResultKind.Succeeded, result.Kind);

            await using var assertions = CreateDbContext(host, generation);
            Assert.Equal("release:backend", (await assertions.Matters.SingleAsync(cancellationToken)).MatterKey);
        }
    }

    [Fact(
        SkipUnless = nameof(PostgresConfigured),
        Skip = "Set ConnectionString in .scratch/postgres-tests.json to an isolated PostgreSQL server with CREATE DATABASE permission."
    )]
    public async Task RuleEvent_ConcurrentEnqueueStartsOneRoundAndQueuesTheOtherInPostgres()
    {
        string configuredConnectionString = IsolatedTestConnections.Postgres!;
        var configured = new NpgsqlConnectionStringBuilder(configuredConnectionString);
        string databaseName = $"taskpilot_concurrency_{Guid.CreateVersion7():N}";
        var adminSettings = new NpgsqlConnectionStringBuilder(configuredConnectionString) { Database = "postgres" };

        await using var adminConnection = new NpgsqlConnection(adminSettings.ConnectionString);
        await adminConnection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = adminConnection.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            configured.Database = databaseName;
            (ExtensionRuntimeIntegrationHost firstHost, ExtensionGenerationSnapshot firstGeneration) =
                await CreateHostWithExecutionFacadeAsync(configured.ConnectionString, DatabaseProvider.Postgres);
            await using (firstHost)
            {
                (ExtensionRuntimeIntegrationHost secondHost, ExtensionGenerationSnapshot secondGeneration) =
                    await CreateHostWithExecutionFacadeAsync(
                        configured.ConnectionString,
                        DatabaseProvider.Postgres,
                        seedActualHostTargets: false
                    );
                await using (secondHost)
                {
                    Guid ruleId;
                    await using (TaskPilotDbContext db = CreateDbContext(firstHost, firstGeneration))
                    {
                        TaskPilotRule rule = NewRule();
                        ruleId = rule.Id;
                        db.Rules.Add(rule);
                        db.RuleRevisions.Add(
                            new TaskPilotRuleRevision
                            {
                                RootId = rule.Id,
                                Version = rule.Version,
                                SnapshotJson = JsonSerializer.Serialize(ToRuleDetails(rule), JsonOptions),
                            }
                        );
                        db.UserStates.Add(NewUserState());
                        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                    }

                    Task<ExtensionEventHandlerResult>[] invocations =
                    [
                        firstHost
                            .InvokeEventHandlerAsync(
                                ExtensionId,
                                RuleEventInvocation(ruleId, NewRuleEvent("concurrent-matter")),
                                TestContext.Current.CancellationToken
                            )
                            .AsTask(),
                        secondHost
                            .InvokeEventHandlerAsync(
                                ExtensionId,
                                RuleEventInvocation(ruleId, NewRuleEvent("concurrent-matter")),
                                TestContext.Current.CancellationToken
                            )
                            .AsTask(),
                    ];
                    ExtensionEventHandlerResult[] results = await Task.WhenAll(invocations);
                    Assert.All(results, result => Assert.Equal(ExtensionEventHandlerResultKind.Succeeded, result.Kind));

                    await using TaskPilotDbContext assertions = CreateDbContext(firstHost, firstGeneration);
                    TaskPilotMatter matter = await assertions.Matters.SingleAsync(
                        row => row.MatterKey == "concurrent-matter",
                        TestContext.Current.CancellationToken
                    );
                    Assert.Equal(
                        2,
                        await assertions.Rounds.CountAsync(
                            round => round.RootId == matter.Id,
                            TestContext.Current.CancellationToken
                        )
                    );
                    Assert.Equal(
                        1,
                        await assertions.Rounds.CountAsync(
                            round => round.RootId == matter.Id && round.State == TaskPilotRoundState.Queued,
                            TestContext.Current.CancellationToken
                        )
                    );
                    Assert.Equal(
                        1,
                        await assertions.MatterActivities.CountAsync(TestContext.Current.CancellationToken)
                    );
                    Assert.Equal(
                        1,
                        await assertions.ConcurrencySlots.CountAsync(TestContext.Current.CancellationToken)
                    );
                }
            }
        }
        finally
        {
            await using var drop = adminConnection.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RuleEndpoints_InvalidManagedFilter_StoresRestrictionAndReason()
    {
        var (host, generation) = await CreateHostAsync();
        await using var runtimeHost = host;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "TaskPilotRuntimeTest")
            )
        );
        using var filter = JsonDocument.Parse("""{"===":[{"var":"data"},{"state":"open"}]}""");
        var request = new TaskPilotRuleWriteRequest
        {
            Name = "Invalid managed filter",
            Kind = TaskPilotRuleKind.Agent,
            SourceIds = [SourceId],
            EventTypes = ["taskpilot.filter"],
            Condition = filter.RootElement,
            TargetKind = AgentTargetKind.Agent,
            TargetId = AgentId,
            ProjectId = ProjectId,
            Instructions = "Process events.",
            GroupKey = "taskpilot-filter-test",
            MaxConcurrency = 1,
            SessionPolicy = TaskPilotSessionPolicy.PerMatter,
        };
        var cancellationToken = TestContext.Current.CancellationToken;
        var created = await host.InvokeEndpointAsync(
            ExtensionId,
            "rules-create",
            JsonSerializer.SerializeToElement(
                new
                {
                    method = "POST",
                    query = new Dictionary<string, string[]>(),
                    body = request,
                },
                JsonOptions
            ),
            cancellationToken
        );
        Assert.Equal(200, created.StatusCode);
        var ruleId = Assert.IsType<JsonElement>(created.JsonBody).GetProperty("rule").GetProperty("id").GetGuid();

        var error = await WaitForManagedSubscriptionErrorAsync(host, generation, ruleId, cancellationToken);

        Assert.Contains("Single-key object literals are not supported", error);
        Assert.Contains("Cannot identify rule for state", error);
    }

    [Fact]
    public async Task RuleEndpoints_CreateUpdatePauseResumeDelete_ManageSubscriptionAndIsolateUsers()
    {
        (ExtensionRuntimeIntegrationHost host, ExtensionGenerationSnapshot generation) = await CreateHostAsync();
        await using (host)
        {
            using IDisposable userScope = UserInfoUtil.Push(
                new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "TaskPilotRuntimeTest")
                )
            );
            string outerUserId = UserInfoUtil.RequiredUserId;
            TaskPilotRuleWriteRequest createRequest = new()
            {
                Name = "TaskPilot lifecycle rule",
                Kind = TaskPilotRuleKind.Agent,
                SourceIds = [SourceId],
                EventTypes = ["taskpilot.lifecycle"],
                TargetKind = AgentTargetKind.Agent,
                TargetId = AgentId,
                ProjectId = ProjectId,
                Instructions = "Process lifecycle events.",
                GroupKey = "taskpilot-lifecycle-test",
                MaxConcurrency = 1,
                SessionPolicy = TaskPilotSessionPolicy.PerMatter,
            };
            ExtensionRuntimeIntegrationResponse created = await host.InvokeEndpointAsync(
                ExtensionId,
                "rules-create",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        method = "POST",
                        query = new Dictionary<string, string[]>(),
                        body = createRequest,
                    },
                    JsonOptions
                ),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(outerUserId, UserInfoUtil.RequiredUserId);
            Assert.Equal(200, created.StatusCode);
            JsonElement createdBody =
                created.JsonBody ?? throw new InvalidOperationException("The rule response body is missing.");
            JsonElement createdRule = createdBody.GetProperty("rule");
            Guid ruleId = Guid.Parse(createdRule.GetProperty("id").GetString()!);
            Assert.Equal(1, createdRule.GetProperty("version").GetInt64());

            EventSubscription createdSubscription = await WaitForManagedSubscriptionAsync(
                host,
                $"rule:{ruleId:D}",
                1,
                EventSubscriptionManagedState.Active
            );
            Assert.Equal(outerUserId, UserInfoUtil.RequiredUserId);
            string createKey = $"rule:{ruleId:D}:subscription:1";
            Assert.Equal("Completed", await ReadOutboxStateAsync(host, createKey));
            Assert.Equal(outerUserId, UserInfoUtil.RequiredUserId);
            (string createIdempotencyKey, string createPayload) = await FindOutboxAsync(
                host,
                "UpsertManagedSubscription",
                createKey
            );
            Assert.Equal(createKey, createIdempotencyKey);
            using (JsonDocument payload = JsonDocument.Parse(createPayload))
            {
                Assert.Equal(1, payload.RootElement.GetProperty("managedVersion").GetInt64());
            }

            TaskPilotRuleWriteRequest updateRequest = createRequest with
            {
                Id = ruleId,
                Version = 1,
                Name = "Updated TaskPilot lifecycle rule",
                EventTypes = ["taskpilot.lifecycle.updated"],
            };
            ExtensionRuntimeIntegrationResponse updated = await host.InvokeEndpointAsync(
                ExtensionId,
                "rules-update",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        method = "POST",
                        query = new Dictionary<string, string[]>(),
                        body = updateRequest,
                    },
                    JsonOptions
                ),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(outerUserId, UserInfoUtil.RequiredUserId);
            Assert.Equal(200, updated.StatusCode);
            JsonElement updatedRule = (
                updated.JsonBody ?? throw new InvalidOperationException("The rule response body is missing.")
            ).GetProperty("rule");
            Assert.Equal("Updated TaskPilot lifecycle rule", updatedRule.GetProperty("name").GetString());
            Assert.Equal(2, updatedRule.GetProperty("version").GetInt64());

            EventSubscription updatedSubscription = await WaitForManagedSubscriptionAsync(
                host,
                $"rule:{ruleId:D}",
                2,
                EventSubscriptionManagedState.Active
            );
            Assert.Equal(outerUserId, UserInfoUtil.RequiredUserId);
            Assert.Equal(createdSubscription.Id, updatedSubscription.Id);
            string updateKey = $"rule:{ruleId:D}:subscription:2";
            Assert.Equal("Completed", await ReadOutboxStateAsync(host, updateKey));
            Assert.Equal(outerUserId, UserInfoUtil.RequiredUserId);
            (string updateIdempotencyKey, string updatePayload) = await FindOutboxAsync(
                host,
                "UpsertManagedSubscription",
                updateKey
            );
            Assert.Equal(updateKey, updateIdempotencyKey);
            using (JsonDocument payload = JsonDocument.Parse(updatePayload))
            {
                Assert.Equal(2, payload.RootElement.GetProperty("managedVersion").GetInt64());
            }

            ExtensionRuntimeIntegrationResponse paused = await host.InvokeEndpointAsync(
                ExtensionId,
                "rules-pause",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        method = "POST",
                        query = new Dictionary<string, string[]>(),
                        body = new TaskPilotVersionRequest { Id = ruleId, Version = 2 },
                    },
                    JsonOptions
                ),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(outerUserId, UserInfoUtil.RequiredUserId);
            Assert.Equal(200, paused.StatusCode);
            JsonElement pausedRule = (
                paused.JsonBody ?? throw new InvalidOperationException("The rule response body is missing.")
            ).GetProperty("rule");
            Assert.True(pausedRule.GetProperty("paused").GetBoolean());
            Assert.Equal(2, pausedRule.GetProperty("version").GetInt64());

            ExtensionRuntimeIntegrationResponse resumed = await host.InvokeEndpointAsync(
                ExtensionId,
                "rules-resume",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        method = "POST",
                        query = new Dictionary<string, string[]>(),
                        body = new TaskPilotVersionRequest { Id = ruleId, Version = 2 },
                    },
                    JsonOptions
                ),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(outerUserId, UserInfoUtil.RequiredUserId);
            Assert.Equal(200, resumed.StatusCode);
            JsonElement resumedRule = (
                resumed.JsonBody ?? throw new InvalidOperationException("The rule response body is missing.")
            ).GetProperty("rule");
            Assert.False(resumedRule.GetProperty("paused").GetBoolean());
            Assert.Equal(2, resumedRule.GetProperty("version").GetInt64());

            using (
                UserInfoUtil.Push(
                    new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, "taskpilot-other-user")],
                            "TaskPilotRuntimeTest"
                        )
                    )
                )
            )
            {
                Assert.Equal("taskpilot-other-user", UserInfoUtil.RequiredUserId);
                AgwException notFound = await Assert.ThrowsAsync<AgwException>(async () =>
                    await host.InvokeEndpointAsync(
                        ExtensionId,
                        "rules-get",
                        JsonSerializer.SerializeToElement(
                            new
                            {
                                method = "GET",
                                query = new Dictionary<string, string[]> { ["id"] = [ruleId.ToString("D")] },
                                body = (object?)null,
                            },
                            JsonOptions
                        ),
                        TestContext.Current.CancellationToken
                    )
                );
                Assert.Equal("taskpilot-other-user", UserInfoUtil.RequiredUserId);
                Assert.Equal(404, (int)notFound.StatusCode);
            }
            Assert.Equal(outerUserId, UserInfoUtil.RequiredUserId);

            ExtensionRuntimeIntegrationResponse deleted = await host.InvokeEndpointAsync(
                ExtensionId,
                "rules-delete",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        method = "POST",
                        query = new Dictionary<string, string[]>(),
                        body = new TaskPilotVersionRequest { Id = ruleId, Version = 2 },
                    },
                    JsonOptions
                ),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(outerUserId, UserInfoUtil.RequiredUserId);
            Assert.Equal(200, deleted.StatusCode);
            JsonElement deletedRule = (
                deleted.JsonBody ?? throw new InvalidOperationException("The rule response body is missing.")
            ).GetProperty("rule");
            Assert.NotNull(deletedRule.GetProperty("deletedAt").GetString());
            Assert.Equal(3, deletedRule.GetProperty("version").GetInt64());

            EventSubscription deletedSubscription = await WaitForManagedSubscriptionAsync(
                host,
                $"rule:{ruleId:D}",
                3,
                EventSubscriptionManagedState.Deleted
            );
            Assert.Equal(outerUserId, UserInfoUtil.RequiredUserId);
            Assert.Equal(createdSubscription.Id, deletedSubscription.Id);
            string deleteKey = $"rule:{ruleId:D}:subscription:3";
            Assert.Equal("Completed", await ReadOutboxStateAsync(host, deleteKey));
            Assert.Equal(outerUserId, UserInfoUtil.RequiredUserId);
            (string deleteIdempotencyKey, string deletePayload) = await FindOutboxAsync(
                host,
                "DeleteManagedSubscription",
                deleteKey
            );
            Assert.Equal(deleteKey, deleteIdempotencyKey);
            using (JsonDocument payload = JsonDocument.Parse(deletePayload))
            {
                Assert.Equal(3, payload.RootElement.GetProperty("managedVersion").GetInt64());
            }

            await using TaskPilotDbContext assertions = CreateDbContext(host, generation);
            TaskPilotRule storedRule = await assertions.Rules.SingleAsync(
                row => row.Id == ruleId,
                TestContext.Current.CancellationToken
            );
            Assert.Equal(3, storedRule.Version);
            Assert.Null(storedRule.SubscriptionId);
            Assert.Null(storedRule.SubscriptionOutboxId);
            Assert.Null(storedRule.SubscriptionError);
            Assert.Equal(
                2,
                await assertions.RuleRevisions.CountAsync(
                    row => row.RootId == ruleId,
                    TestContext.Current.CancellationToken
                )
            );
        }
    }

    [Fact]
    public async Task ReviewCancel_CancelsRealScheduleAndCompletesOutbox()
    {
        (ExtensionRuntimeIntegrationHost host, ExtensionGenerationSnapshot generation) = await CreateHostAsync();
        await using (host)
        {
            using IDisposable userScope = UserInfoUtil.Push(
                new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "TaskPilotRuntimeTest")
                )
            );
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            Guid ruleId = Guid.CreateVersion7();
            Guid matterId = Guid.CreateVersion7();
            Guid reviewId = Guid.CreateVersion7();
            Guid scheduleId = Guid.CreateVersion7();
            const string reviewKey = "weekly";
            string businessKey = $"review:{matterId:D}:{reviewKey}";
            string idempotencyKey = $"taskpilot:review:{reviewId:D}:cancel:2";
            DateTimeOffset dueAt = TimeProvider.System.GetUtcNow().AddHours(1);

            await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
            {
                AgwDbContext events = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
                events.EventSchedules.Add(
                    new EventSchedule
                    {
                        Id = scheduleId,
                        CreateBy = UserId,
                        CallerKey = $"ext:{ExtensionId:D}",
                        BusinessKey = businessKey,
                        Kind = EventScheduleKind.Once,
                        DueAt = dueAt,
                        TimeZone = "UTC",
                        EventType = "taskpilot.review.due",
                        ChainId = scheduleId,
                        State = EventScheduleState.Active,
                        RequestSeq = 1,
                        Revision = 1,
                        Version = 1,
                    }
                );
                await events.SaveChangesAsync(cancellationToken);
            }

            await using (TaskPilotDbContext db = CreateDbContext(host, generation))
            {
                TaskPilotRule rule = NewRule(paused: true);
                rule.Id = ruleId;
                db.Rules.Add(rule);
                db.Matters.Add(
                    new TaskPilotMatter
                    {
                        Id = matterId,
                        RootId = rule.Id,
                        MatterKey = "review-cancel",
                        State = TaskPilotMatterState.Active,
                        Version = 1,
                    }
                );
                db.Reviews.Add(
                    new TaskPilotReview
                    {
                        Id = reviewId,
                        RootId = matterId,
                        ReviewKey = reviewKey,
                        DueAt = dueAt,
                        State = TaskPilotReviewState.Scheduled,
                        RequestSeq = 1,
                        ScheduleId = scheduleId,
                        Version = 1,
                    }
                );
                db.UserStates.Add(NewUserState());
                await db.SaveChangesAsync(cancellationToken);
            }

            ExtensionRuntimeIntegrationResponse response = await host.InvokeEndpointAsync(
                ExtensionId,
                "reviews-cancel",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        method = "POST",
                        query = new Dictionary<string, string[]>(),
                        body = new TaskPilotVersionRequest { Id = reviewId, Version = 1 },
                    },
                    JsonOptions
                ),
                cancellationToken
            );

            Assert.Equal(200, response.StatusCode);
            await using (TaskPilotDbContext assertions = CreateDbContext(host, generation))
            {
                TaskPilotReview canceledReview = await assertions.Reviews.SingleAsync(
                    row => row.Id == reviewId,
                    cancellationToken
                );
                Assert.Equal(TaskPilotReviewState.Cancelled, canceledReview.State);
                Assert.Equal(2, canceledReview.RequestSeq);
            }

            (string outboxKey, string payloadJson) = await FindOutboxAsync(host, "CancelSchedule", idempotencyKey);
            Assert.Equal(idempotencyKey, outboxKey);
            using (JsonDocument payload = JsonDocument.Parse(payloadJson))
            {
                Assert.Equal(businessKey, payload.RootElement.GetProperty("businessKey").GetString());
                Assert.Equal(2, payload.RootElement.GetProperty("requestSeq").GetInt64());
            }

            DateTimeOffset deadline = TimeProvider.System.GetUtcNow().AddSeconds(15);
            EventSchedule? storedSchedule = null;
            string outboxState = string.Empty;
            while (TimeProvider.System.GetUtcNow() < deadline)
            {
                await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
                AgwDbContext events = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
                storedSchedule = await events.EventSchedules.SingleOrDefaultAsync(
                    row => row.CallerKey == $"ext:{ExtensionId:D}" && row.BusinessKey == businessKey,
                    cancellationToken
                );
                outboxState = await ReadOutboxStateAsync(host, idempotencyKey);
                if (storedSchedule?.State == EventScheduleState.Cancelled && outboxState == "Completed")
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }

            Assert.True(
                storedSchedule is { State: EventScheduleState.Cancelled, RequestSeq: 2 } && outboxState == "Completed",
                $"The review schedule cancellation did not complete. Outbox={await ReadOutboxDiagnosticAsync(host, idempotencyKey)}"
            );
        }
    }

    [Fact]
    public async Task CancelEndpoint_RunningRoundRetainsItsActivityAndConcurrencySlot()
    {
        (ExtensionRuntimeIntegrationHost host, ExtensionGenerationSnapshot generation) = await CreateHostAsync();
        await using (host)
        {
            SeededRound seeded = await SeedRunningRoundAsync(host, generation);
            JsonElement request = JsonSerializer.SerializeToElement(
                new
                {
                    method = "POST",
                    query = new Dictionary<string, string[]>(),
                    body = new TaskPilotMatterCancelRequest
                    {
                        Id = seeded.MatterId,
                        Version = 1,
                        RoundId = seeded.RoundId,
                    },
                },
                JsonOptions
            );
            using IDisposable userScope = UserInfoUtil.Push(
                new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "TaskPilotRuntimeTest")
                )
            );

            ExtensionRuntimeIntegrationResponse response = await host.InvokeEndpointAsync(
                ExtensionId,
                "matters-cancel",
                request,
                TestContext.Current.CancellationToken
            );

            Assert.Equal(200, response.StatusCode);
            await using TaskPilotDbContext assertions = CreateDbContext(host, generation);
            TaskPilotRound round = await assertions.Rounds.SingleAsync(
                row => row.Id == seeded.RoundId,
                TestContext.Current.CancellationToken
            );
            Assert.Equal(TaskPilotRoundState.Cancelling, round.State);
            Assert.NotNull(
                (
                    await assertions.RoundAttempts.SingleAsync(
                        row => row.RootId == seeded.RoundId,
                        TestContext.Current.CancellationToken
                    )
                ).CancelOutboxId
            );
            Assert.Equal(1, await assertions.MatterActivities.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await assertions.ConcurrencySlots.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task SubmitExecutionRejectedByRealAgentCatalog_ReleasesItsActivityAndSlot()
    {
        string databasePath = Path.Combine(
            AppContext.BaseDirectory,
            $"taskpilot-rejection-{Guid.CreateVersion7():N}.db"
        );
        string connectionString = $"Data Source={databasePath}";
        try
        {
            (ExtensionRuntimeIntegrationHost host, ExtensionGenerationSnapshot generation) =
                await CreateHostWithExecutionFacadeAsync(connectionString);
            await using (host)
            {
                Guid ruleId;
                await using (TaskPilotDbContext db = CreateDbContext(host, generation))
                {
                    TaskPilotRule rule = NewRule();
                    rule.TargetId = Guid.CreateVersion7();
                    ruleId = rule.Id;
                    db.Rules.Add(rule);
                    db.RuleRevisions.Add(
                        new TaskPilotRuleRevision
                        {
                            RootId = rule.Id,
                            Version = rule.Version,
                            SnapshotJson = JsonSerializer.Serialize(ToRuleDetails(rule), JsonOptions),
                        }
                    );
                    db.UserStates.Add(NewUserState());
                    await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                }

                await host.InvokeEventHandlerAsync(
                    ExtensionId,
                    RuleEventInvocation(ruleId, NewRuleEvent("missing-agent")),
                    TestContext.Current.CancellationToken
                );
                Guid roundId;
                await using (TaskPilotDbContext db = CreateDbContext(host, generation))
                {
                    roundId = await db
                        .Rounds.Select(round => round.Id)
                        .SingleAsync(TestContext.Current.CancellationToken);
                }

                await WaitForRoundStateAsync(host, generation, roundId, TaskPilotRoundState.Failed);

                await using TaskPilotDbContext assertions = CreateDbContext(host, generation);
                Assert.Equal(0, await assertions.MatterActivities.CountAsync(TestContext.Current.CancellationToken));
                Assert.Equal(0, await assertions.ConcurrencySlots.CountAsync(TestContext.Current.CancellationToken));
                string state = await ReadOutboxStateAsync(host, $"taskpilot:round:{roundId:D}:attempt:1:submit");
                Assert.Equal("Rejected", state);
            }
        }
        finally
        {
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                string path = databasePath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SystemEvent_CompletionFromWrongSource_LeavesRoundRunning(bool useNotificationPublisher)
    {
        (ExtensionRuntimeIntegrationHost host, ExtensionGenerationSnapshot generation) = await CreateHostAsync();
        await using (host)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var seeded = await SeedRunningRoundAsync(host, generation);
            var invocation = SystemEventInvocation(UserId, NewExecutionCompletedEvent(seeded, "Completed"));
            Guid untrustedSourceId = useNotificationPublisher ? NotificationSourceId : SourceId;
            var ignored = await host.InvokeEventHandlerAsync(
                ExtensionId,
                invocation with
                {
                    Delivery = invocation.Delivery with { SourceId = untrustedSourceId },
                },
                cancellationToken
            );
            Assert.Equal(ExtensionEventHandlerResultKind.Succeeded, ignored.Kind);
            await using (var db = CreateDbContext(host, generation))
            {
                var round = await db.Rounds.SingleAsync(row => row.Id == seeded.RoundId, cancellationToken);
                Assert.Equal(TaskPilotRoundState.Running, round.State);
            }

            var accepted = await host.InvokeEventHandlerAsync(
                ExtensionId,
                invocation with
                {
                    Delivery = invocation.Delivery with { DeliveryId = Guid.CreateVersion7() },
                },
                cancellationToken
            );
            Assert.Equal(ExtensionEventHandlerResultKind.Succeeded, accepted.Kind);
            await using var assertions = CreateDbContext(host, generation);
            var completed = await assertions.Rounds.SingleAsync(row => row.Id == seeded.RoundId, cancellationToken);
            Assert.Equal(TaskPilotRoundState.Completed, completed.State);
        }
    }

    [Fact]
    public async Task ExecutionCompletion_InvalidReceiptDoesNotApplyAnyReceiptEffects()
    {
        (ExtensionRuntimeIntegrationHost host, ExtensionGenerationSnapshot generation) = await CreateHostAsync();
        await using (host)
        {
            SeededRound seeded = await SeedRunningRoundAsync(host, generation);
            ExtensionEventHandlerResult result = await host.InvokeEventHandlerAsync(
                ExtensionId,
                SystemEventInvocation(
                    UserId,
                    NewExecutionCompletedEvent(
                        seeded,
                        "Completed\n```taskpilot\n"
                            + "{\"ask\":[{\"key\":\"question\",\"title\":\"Question\",\"text\":\"Choose\"}],"
                            + "\"later\":[{\"key\":\"review\",\"at\":\"2030-11-09T12:00:00Z\"}],"
                            + "\"events\":[{\"id\":\"event\",\"type\":\"taskpilot.denied\",\"detail\":{\"value\":1}}]}\n```"
                    )
                ),
                TestContext.Current.CancellationToken
            );

            Assert.Equal(ExtensionEventHandlerResultKind.Succeeded, result.Kind);
            await using TaskPilotDbContext assertions = CreateDbContext(host, generation);
            TaskPilotRound round = await assertions.Rounds.SingleAsync(
                row => row.Id == seeded.RoundId,
                TestContext.Current.CancellationToken
            );
            Assert.Equal(TaskPilotRoundState.ReceiptRepair, round.State);
            Assert.Contains("cannot publish", round.ReceiptError, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await assertions.Cards.ToArrayAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await assertions.Reviews.ToArrayAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await CountOutboxAsync(host, "PublishEvent"));
            Assert.Equal(1, await assertions.MatterActivities.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await assertions.ConcurrencySlots.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public Task ExecutionCompletion_WithoutReceipt_ParsesAndUnloads()
    {
        return RunReceiptParserPhaseCaseAsync("PlainCompletion");
    }

    [Fact]
    public Task ExecutionCompletion_WithValidEmptyReceipt_ParsesAndUnloads()
    {
        return RunReceiptParserPhaseCaseAsync("ValidEmptyReceipt");
    }

    [Fact]
    public Task ExecutionCompletion_WithSchemaInvalidReceipt_EntersRepair()
    {
        return RunReceiptParserPhaseCaseAsync("InvalidSchema");
    }

    private static async Task RunReceiptParserPhaseCaseAsync(string caseName)
    {
        (ExtensionRuntimeIntegrationHost host, ExtensionGenerationSnapshot generation) = await CreateHostAsync();
        await using (host)
        {
            SeededRound seeded = await SeedRunningRoundAsync(host, generation);
            string resultText = caseName switch
            {
                "PlainCompletion" => "Completed",
                "ValidEmptyReceipt" => "Completed\n```taskpilot\n{\"events\":[]}\n```",
                "InvalidSchema" => "Completed\n```taskpilot\n{\"events\":[{\"type\":42}]}\n```",
                _ => throw new InvalidOperationException($"Unknown parser probe case {caseName}."),
            };
            ExtensionEventHandlerResult result = await host.InvokeEventHandlerAsync(
                ExtensionId,
                SystemEventInvocation(UserId, NewExecutionCompletedEvent(seeded, resultText)),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(ExtensionEventHandlerResultKind.Succeeded, result.Kind);

            await using TaskPilotDbContext assertions = CreateDbContext(host, generation);
            TaskPilotRound round = await assertions.Rounds.SingleAsync(
                row => row.Id == seeded.RoundId,
                TestContext.Current.CancellationToken
            );
            Assert.Equal(
                caseName == "InvalidSchema" ? TaskPilotRoundState.ReceiptRepair : TaskPilotRoundState.Completed,
                round.State
            );
        }
    }

    [Fact]
    public async Task ExecutionCompletion_UsesMonotonicRetrySequenceAfterSuccessAndFailure()
    {
        (ExtensionRuntimeIntegrationHost host, ExtensionGenerationSnapshot generation) = await CreateHostAsync();
        await using (host)
        {
            SeededRound first = await SeedRunningRoundAsync(
                host,
                generation,
                consecutiveFailures: 1,
                retryRequestSeq: 1
            );
            await host.InvokeEventHandlerAsync(
                ExtensionId,
                SystemEventInvocation(UserId, NewExecutionCompletedEvent(first, "Completed")),
                TestContext.Current.CancellationToken
            );

            await using (TaskPilotDbContext db = CreateDbContext(host, generation))
            {
                TaskPilotMatter matter = await db.Matters.SingleAsync(
                    row => row.Id == first.MatterId,
                    TestContext.Current.CancellationToken
                );
                Assert.Equal(0, matter.ConsecutiveFailures);
                Assert.Equal(2, matter.RetryRequestSeq);

                TaskPilotRound laterRound = NewRunningRound(matter.Id, Guid.CreateVersion7());
                db.Rounds.Add(laterRound);
                db.RoundAttempts.Add(NewRunningAttempt(laterRound));
                db.MatterActivities.Add(
                    new TaskPilotMatterActivity
                    {
                        RootId = matter.Id,
                        RoundId = laterRound.Id,
                        AcquiredAt = TimeProvider.System.GetUtcNow(),
                    }
                );
                db.ConcurrencySlots.Add(
                    new TaskPilotConcurrencySlot
                    {
                        GroupKey = "taskpilot-test",
                        SlotIndex = 0,
                        RoundId = laterRound.Id,
                    }
                );
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                first = first with { LaterRoundId = laterRound.Id, LaterTurnId = laterRound.TurnId!.Value };
            }

            await host.InvokeEventHandlerAsync(
                ExtensionId,
                SystemEventInvocation(UserId, NewExecutionFailedEvent(first.LaterRoundId!.Value, first.LaterTurnId)),
                TestContext.Current.CancellationToken
            );

            await using (TaskPilotDbContext assertions = CreateDbContext(host, generation))
            {
                TaskPilotMatter matter = await assertions.Matters.SingleAsync(
                    row => row.Id == first.MatterId,
                    TestContext.Current.CancellationToken
                );
                Assert.Equal(1, matter.ConsecutiveFailures);
                Assert.Equal(3, matter.RetryRequestSeq);
                (string idempotencyKey, string payloadJson) = await FindOutboxAsync(
                    host,
                    "UpsertSchedule",
                    $"taskpilot:retry:{first.MatterId:D}:3"
                );
                Assert.Equal($"taskpilot:retry:{first.MatterId:D}:3", idempotencyKey);
                using JsonDocument payload = JsonDocument.Parse(payloadJson);
                Assert.Equal(3, payload.RootElement.GetProperty("requestSeq").GetInt64());
            }

            CloudEventEnvelope staleRetry = NewRetryEvent(first.MatterId, first.RoundId, requestSeq: 1);
            ExtensionEventHandlerResult staleResult = await host.InvokeEventHandlerAsync(
                ExtensionId,
                SystemEventInvocation(UserId, staleRetry),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(ExtensionEventHandlerResultKind.Succeeded, staleResult.Kind);
            await using TaskPilotDbContext finalAssertions = CreateDbContext(host, generation);
            Assert.Equal(2, await finalAssertions.Rounds.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ExecutionCompletion_UsesReceiptDetailAsPublishedEventData()
    {
        (ExtensionRuntimeIntegrationHost host, ExtensionGenerationSnapshot generation) = await CreateHostAsync();
        await using (host)
        {
            SeededRound seeded = await SeedRunningRoundAsync(host, generation, emitTypes: ["taskpilot.published"]);
            await host.InvokeEventHandlerAsync(
                ExtensionId,
                SystemEventInvocation(
                    UserId,
                    NewExecutionCompletedEvent(
                        seeded,
                        "Completed\n```taskpilot\n"
                            + "{\"events\":[{\"id\":\"notice-1\",\"type\":\"taskpilot.published\","
                            + "\"scope\":\"project\",\"tags\":[\"taskpilot\"],\"detail\":{\"answer\":42}}]}\n```"
                    )
                ),
                TestContext.Current.CancellationToken
            );

            (string _, string payloadJson) = await FindOutboxAsync(host, "PublishEvent");
            using JsonDocument payload = JsonDocument.Parse(payloadJson);
            JsonElement draft = payload.RootElement;
            Assert.Equal("project", draft.GetProperty("scope").GetString());
            Assert.Equal("taskpilot", draft.GetProperty("tags")[0].GetString());
            JsonElement data = draft.GetProperty("data").GetProperty("json");
            Assert.Equal(42, data.GetProperty("answer").GetInt32());
            Assert.False(data.TryGetProperty("detail", out _));
            Assert.False(data.TryGetProperty("scope", out _));
            Assert.False(data.TryGetProperty("tags", out _));
        }
    }

    [Fact]
    public async Task NotificationAnswer_UsesTheQuestionConversationForItsRound()
    {
        (ExtensionRuntimeIntegrationHost host, ExtensionGenerationSnapshot generation) = await CreateHostAsync();
        await using (host)
        {
            Guid matterId = Guid.CreateVersion7();
            Guid cardId = Guid.CreateVersion7();
            Guid questionConversationId = Guid.CreateVersion7();
            Guid matterConversationId = Guid.CreateVersion7();
            Guid notificationId = Guid.CreateVersion7();
            await using (TaskPilotDbContext db = CreateDbContext(host, generation))
            {
                TaskPilotRule rule = NewRule(paused: true);
                TaskPilotMatter matter = new()
                {
                    Id = matterId,
                    RootId = rule.Id,
                    MatterKey = "answered-question",
                    State = TaskPilotMatterState.WaitingHuman,
                    ConversationId = matterConversationId,
                    Version = 1,
                };
                TaskPilotRound askingRound = new()
                {
                    RootId = matter.Id,
                    RuleVersion = rule.Version,
                    State = TaskPilotRoundState.Completed,
                    Trigger = TaskPilotRoundTrigger.Event,
                    ConversationId = Guid.CreateVersion7(),
                    Version = 1,
                };
                db.Rules.Add(rule);
                db.Matters.Add(matter);
                db.Rounds.Add(askingRound);
                db.Cards.Add(
                    new TaskPilotCard
                    {
                        Id = cardId,
                        RootId = matter.Id,
                        RoundId = askingRound.Id,
                        Kind = TaskPilotCardKind.Ask,
                        CardKey = "ask:question",
                        OpenCardKey = "ask:question",
                        Title = "Question",
                        Text = "Choose",
                        State = TaskPilotCardState.Open,
                        NotificationId = notificationId,
                        ConversationId = questionConversationId,
                    }
                );
                db.UserStates.Add(NewUserState());
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            ExtensionEventHandlerResult result = await host.InvokeEventHandlerAsync(
                ExtensionId,
                SystemEventInvocation(
                    UserId,
                    NewCloudEvent(
                        "agw.notification.answered",
                        new
                        {
                            origin = ExtensionId.ToString("D"),
                            correlationKey = $"card:{cardId:D}",
                            notificationId,
                            option = 0,
                            text = (string?)null,
                        }
                    )
                ),
                TestContext.Current.CancellationToken
            );

            Assert.Equal(ExtensionEventHandlerResultKind.Succeeded, result.Kind);
            await using TaskPilotDbContext assertions = CreateDbContext(host, generation);
            TaskPilotRound answerRound = await assertions.Rounds.SingleAsync(
                row => row.Trigger == TaskPilotRoundTrigger.Answer,
                TestContext.Current.CancellationToken
            );
            Assert.Equal(questionConversationId, answerRound.ConversationId);
            Assert.NotEqual(matterConversationId, answerRound.ConversationId);
        }
    }

    [Fact]
    public async Task ReviewDue_StaleRequestSequenceDoesNotCreateARound()
    {
        (ExtensionRuntimeIntegrationHost host, ExtensionGenerationSnapshot generation) = await CreateHostAsync();
        await using (host)
        {
            Guid matterId = Guid.CreateVersion7();
            Guid reviewId = Guid.CreateVersion7();
            await using (TaskPilotDbContext db = CreateDbContext(host, generation))
            {
                TaskPilotRule rule = NewRule(paused: true);
                TaskPilotMatter matter = new()
                {
                    Id = matterId,
                    RootId = rule.Id,
                    MatterKey = "review-stale",
                    State = TaskPilotMatterState.Active,
                    Version = 1,
                };
                db.Rules.Add(rule);
                db.Matters.Add(matter);
                db.Reviews.Add(
                    new TaskPilotReview
                    {
                        Id = reviewId,
                        RootId = matter.Id,
                        ReviewKey = "weekly",
                        State = TaskPilotReviewState.Scheduled,
                        RequestSeq = 2,
                        DueAt = TimeProvider.System.GetUtcNow().AddMinutes(-1),
                    }
                );
                db.UserStates.Add(NewUserState());
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            ExtensionEventHandlerResult result = await host.InvokeEventHandlerAsync(
                ExtensionId,
                SystemEventInvocation(
                    UserId,
                    NewCloudEvent(
                        "taskpilot.review.due",
                        new
                        {
                            matterId,
                            reviewId,
                            requestSeq = 1L,
                        },
                        matterId.ToString("D")
                    )
                ),
                TestContext.Current.CancellationToken
            );

            Assert.Equal(ExtensionEventHandlerResultKind.Succeeded, result.Kind);
            await using TaskPilotDbContext assertions = CreateDbContext(host, generation);
            Assert.Empty(await assertions.Rounds.ToArrayAsync(TestContext.Current.CancellationToken));
            TaskPilotReview review = await assertions.Reviews.SingleAsync(
                row => row.Id == reviewId,
                TestContext.Current.CancellationToken
            );
            Assert.Equal(TaskPilotReviewState.Scheduled, review.State);
            Assert.Equal(2, review.RequestSeq);
        }
    }

    [Fact]
    public async Task DeliveryReviewCompletion_PersistsBeforeUnavailableStatusUpdateAndTickRetries()
    {
        (ExtensionRuntimeIntegrationHost host, ExtensionGenerationSnapshot generation) = await CreateHostAsync();
        await using (host)
        {
            Guid deliveryId = Guid.CreateVersion7();
            Guid roundId = Guid.CreateVersion7();
            Guid turnId = Guid.CreateVersion7();
            Guid connectionId = Guid.CreateVersion7();
            const string headSha = "a8b4d3275ecf4d20b8f6f4e2c78b1e29f704afcd";
            long completionScheduleVersion;
            await using (TaskPilotDbContext db = CreateDbContext(host, generation))
            {
                TaskPilotRule rule = NewRule();
                rule.Kind = TaskPilotRuleKind.Delivery;
                rule.DeliveryJson = JsonSerializer.Serialize(
                    new
                    {
                        connectionId,
                        repository = "acme/app",
                        checkIntervalSeconds = 60,
                    },
                    JsonOptions
                );
                TaskPilotMatter matter = new()
                {
                    RootId = rule.Id,
                    MatterKey = "acme/app#17",
                    Title = "acme/app#17",
                    State = TaskPilotMatterState.Active,
                    Version = 1,
                };
                TaskPilotRound seededReviewRound = new()
                {
                    Id = roundId,
                    RootId = matter.Id,
                    RuleVersion = rule.Version,
                    State = TaskPilotRoundState.Running,
                    Trigger = TaskPilotRoundTrigger.Review,
                    TurnId = turnId,
                    Attempt = 1,
                    Version = 1,
                };
                db.Rules.Add(rule);
                db.Matters.Add(matter);
                db.Deliveries.Add(
                    new TaskPilotDelivery
                    {
                        Id = deliveryId,
                        RootId = matter.Id,
                        Repository = "acme/app",
                        PullRequestNumber = 17,
                        ReviewRoundId = seededReviewRound.Id,
                        Stage = "Review",
                        HeadSha = headSha,
                        Version = 1,
                    }
                );
                db.Rounds.Add(seededReviewRound);
                db.RoundAttempts.Add(
                    new TaskPilotRoundAttempt
                    {
                        RootId = seededReviewRound.Id,
                        Attempt = 1,
                        TurnId = turnId,
                        Purpose = TaskPilotAttemptPurpose.DeliveryReview,
                        State = TaskPilotAttemptState.Running,
                    }
                );
                db.MatterActivities.Add(
                    new TaskPilotMatterActivity
                    {
                        RootId = matter.Id,
                        RoundId = seededReviewRound.Id,
                        AcquiredAt = TimeProvider.System.GetUtcNow(),
                    }
                );
                db.ConcurrencySlots.Add(
                    new TaskPilotConcurrencySlot
                    {
                        GroupKey = rule.GroupKey,
                        SlotIndex = 0,
                        RoundId = seededReviewRound.Id,
                    }
                );
                db.UserStates.Add(NewUserState());
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            ExtensionEventHandlerResult completion = await host.InvokeEventHandlerAsync(
                ExtensionId,
                SystemEventInvocation(
                    UserId,
                    NewCloudEvent(
                        "agw.execution.completed",
                        new
                        {
                            caller = $"ext:{ExtensionId:D}",
                            correlationKey = $"delivery-review:{deliveryId:D}:{headSha}",
                            turnId,
                            state = "Completed",
                            resultText = "Review complete\n```taskpilot-review\n"
                                + "{\"approved\":true,\"summary\":\"Review passed\",\"findings\":[]}\n```",
                            inputTokens = 11L,
                            outputTokens = 7L,
                        }
                    )
                ),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(ExtensionEventHandlerResultKind.Succeeded, completion.Kind);

            await using (TaskPilotDbContext afterCompletion = CreateDbContext(host, generation))
            {
                TaskPilotDelivery delivery = await afterCompletion.Deliveries.SingleAsync(
                    row => row.Id == deliveryId,
                    TestContext.Current.CancellationToken
                );
                TaskPilotRound round = await afterCompletion.Rounds.SingleAsync(
                    row => row.Id == roundId,
                    TestContext.Current.CancellationToken
                );
                TaskPilotRoundAttempt attempt = await afterCompletion.RoundAttempts.SingleAsync(
                    row => row.RootId == roundId,
                    TestContext.Current.CancellationToken
                );
                Assert.Equal("Review", delivery.Stage);
                Assert.Equal(headSha, delivery.ReviewedSha);
                Assert.Contains("Review passed", delivery.FindingsJson);
                Assert.Equal(TaskPilotRoundState.Completed, round.State);
                Assert.Equal(TaskPilotAttemptState.Completed, attempt.State);
                Assert.Equal(
                    0,
                    await afterCompletion.MatterActivities.CountAsync(TestContext.Current.CancellationToken)
                );
                Assert.Equal(
                    0,
                    await afterCompletion.ConcurrencySlots.CountAsync(TestContext.Current.CancellationToken)
                );
                completionScheduleVersion = delivery.Version;
                Assert.True(completionScheduleVersion > 1);
            }

            using IDisposable userScope = UserInfoUtil.Push(
                new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "TaskPilotRuntimeTest")
                )
            );
            ExtensionEventHandlerResult tick = await host.InvokeEventHandlerAsync(
                ExtensionId,
                SystemEventInvocation(
                    UserId,
                    NewCloudEvent(
                        "taskpilot.delivery.tick",
                        new { deliveryId, version = completionScheduleVersion },
                        deliveryId.ToString("D")
                    )
                ),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(ExtensionEventHandlerResultKind.Succeeded, tick.Kind);

            await using TaskPilotDbContext assertions = CreateDbContext(host, generation);
            TaskPilotDelivery afterTick = await assertions.Deliveries.SingleAsync(
                row => row.Id == deliveryId,
                TestContext.Current.CancellationToken
            );
            Assert.Equal("Review", afterTick.Stage);
            Assert.Equal(headSha, afterTick.ReviewedSha);
            Assert.Contains("not found", afterTick.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.True(afterTick.Version > completionScheduleVersion);
            Assert.True(
                await assertions.DeliveryStages.AnyAsync(
                    row => row.RootId == deliveryId && row.Result == "StatusPending",
                    TestContext.Current.CancellationToken
                )
            );
            (string idempotencyKey, string payloadJson) = await FindOutboxAsync(
                host,
                "UpsertSchedule",
                $"delivery:{deliveryId:D}:tick:{afterTick.Version}"
            );
            Assert.Equal($"delivery:{deliveryId:D}:tick:{afterTick.Version}", idempotencyKey);
            using JsonDocument schedule = JsonDocument.Parse(payloadJson);
            Assert.Equal(afterTick.Version, schedule.RootElement.GetProperty("requestSeq").GetInt64());
        }
    }

    [Fact]
    public async Task DeliveryReviewCompletion_NullFindingFailsRoundAndReleasesItsSlot()
    {
        (ExtensionRuntimeIntegrationHost host, ExtensionGenerationSnapshot generation) = await CreateHostAsync();
        await using (host)
        {
            Guid deliveryId = Guid.CreateVersion7();
            Guid roundId = Guid.CreateVersion7();
            Guid turnId = Guid.CreateVersion7();
            const string headSha = "a8b4d3275ecf4d20b8f6f4e2c78b1e29f704afcd";
            await using (TaskPilotDbContext db = CreateDbContext(host, generation))
            {
                TaskPilotRule rule = NewRule();
                rule.Kind = TaskPilotRuleKind.Delivery;
                rule.DeliveryJson = JsonSerializer.Serialize(
                    new
                    {
                        connectionId = Guid.CreateVersion7(),
                        repository = "acme/app",
                        checkIntervalSeconds = 60,
                    },
                    JsonOptions
                );
                TaskPilotMatter matter = new()
                {
                    RootId = rule.Id,
                    MatterKey = "acme/app#18",
                    Title = "acme/app#18",
                    State = TaskPilotMatterState.Active,
                    Version = 1,
                };
                TaskPilotRound seededReviewRound = new()
                {
                    Id = roundId,
                    RootId = matter.Id,
                    RuleVersion = rule.Version,
                    State = TaskPilotRoundState.Running,
                    Trigger = TaskPilotRoundTrigger.Review,
                    TurnId = turnId,
                    Attempt = 1,
                    Version = 1,
                };
                db.Rules.Add(rule);
                db.Matters.Add(matter);
                db.Deliveries.Add(
                    new TaskPilotDelivery
                    {
                        Id = deliveryId,
                        RootId = matter.Id,
                        Repository = "acme/app",
                        PullRequestNumber = 18,
                        ReviewRoundId = seededReviewRound.Id,
                        Stage = "Review",
                        HeadSha = headSha,
                        Version = 1,
                    }
                );
                db.Rounds.Add(seededReviewRound);
                db.RoundAttempts.Add(
                    new TaskPilotRoundAttempt
                    {
                        RootId = seededReviewRound.Id,
                        Attempt = 1,
                        TurnId = turnId,
                        Purpose = TaskPilotAttemptPurpose.DeliveryReview,
                        State = TaskPilotAttemptState.Running,
                    }
                );
                db.MatterActivities.Add(
                    new TaskPilotMatterActivity
                    {
                        RootId = matter.Id,
                        RoundId = seededReviewRound.Id,
                        AcquiredAt = TimeProvider.System.GetUtcNow(),
                    }
                );
                db.ConcurrencySlots.Add(
                    new TaskPilotConcurrencySlot
                    {
                        GroupKey = rule.GroupKey,
                        SlotIndex = 0,
                        RoundId = seededReviewRound.Id,
                    }
                );
                db.UserStates.Add(NewUserState());
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            ExtensionEventHandlerResult result = await host.InvokeEventHandlerAsync(
                ExtensionId,
                SystemEventInvocation(
                    UserId,
                    NewCloudEvent(
                        "agw.execution.completed",
                        new
                        {
                            caller = $"ext:{ExtensionId:D}",
                            correlationKey = $"delivery-review:{deliveryId:D}:{headSha}",
                            turnId,
                            state = "Completed",
                            resultText = "Review complete\n```taskpilot-review\n"
                                + "{\"approved\":false,\"summary\":\"Malformed findings\",\"findings\":[null]}\n```",
                            inputTokens = 11L,
                            outputTokens = 7L,
                        }
                    )
                ),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(ExtensionEventHandlerResultKind.Succeeded, result.Kind);

            await using TaskPilotDbContext assertions = CreateDbContext(host, generation);
            TaskPilotDelivery delivery = await assertions.Deliveries.SingleAsync(
                row => row.Id == deliveryId,
                TestContext.Current.CancellationToken
            );
            TaskPilotRound round = await assertions.Rounds.SingleAsync(
                row => row.Id == roundId,
                TestContext.Current.CancellationToken
            );
            TaskPilotRoundAttempt attempt = await assertions.RoundAttempts.SingleAsync(
                row => row.RootId == roundId,
                TestContext.Current.CancellationToken
            );
            Assert.Equal("Failed", delivery.Stage);
            Assert.NotNull(delivery.LastError);
            Assert.Equal(TaskPilotRoundState.Failed, round.State);
            Assert.Equal(TaskPilotAttemptState.Failed, attempt.State);
            Assert.Equal(0, await assertions.MatterActivities.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await assertions.ConcurrencySlots.CountAsync(TestContext.Current.CancellationToken));
            Assert.True(
                await assertions.DeliveryStages.AnyAsync(
                    row => row.RootId == deliveryId && row.Result == "ReviewFailed",
                    TestContext.Current.CancellationToken
                )
            );
        }
    }

    private static async Task<(
        ExtensionRuntimeIntegrationHost Host,
        ExtensionGenerationSnapshot Generation
    )> CreateHostAsync() => await CreateHostWithExecutionFacadeAsync(null);

    private static async Task<(
        ExtensionRuntimeIntegrationHost Host,
        ExtensionGenerationSnapshot Generation
    )> CreateHostWithExecutionFacadeAsync(
        string? connectionString,
        DatabaseProvider databaseProvider = DatabaseProvider.Sqlite,
        bool seedActualHostTargets = true
    )
    {
        string packageDirectory = Path.Combine(FindRepositoryRoot(), "src", "extensions", "taskpilot", "dist");
        var configurationValues = new Dictionary<string, string?>
        {
            ["Database:Provider"] = databaseProvider == DatabaseProvider.Sqlite ? "sqlite" : "postgres",
            ["Execution:Provider"] = "InProcess",
            ["Events:Transport:Provider"] = "InProcess",
        };
        if (connectionString is not null)
        {
            configurationValues["Database:ConnectionString"] = connectionString;
        }
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(configurationValues).Build();
        ExtensionRuntimeIntegrationHost host = await ExtensionRuntimeIntegrationHost.CreateAsync(
            packageDirectory,
            services =>
            {
                services.AddTools(configuration);
                services.AddAgents(configuration);
                services.AddAgentExecution(
                    configuration,
                    new Agw.Agents.Execution.DependencyInjection.RegistrationOptions(
                        AddExecutionTransport: false,
                        AddDistributedWorker: false,
                        AddTraceCollector: false
                    )
                );
                services.AddFiles(configuration);
                services.AddInfrastructure(configuration);
                services.AddLogging(builder => builder.AddConsole());
                services.AddExtensionCapabilities();
                services.AddEvents(runWorkers: false);
                services.AddNotifications(configuration, runWorkers: false);
                services.AddProviders(configuration);
                services.AddSkills(configuration);
                services.AddAuth();
                services.AddSetup(configuration);
                services.AddIntegrations(configuration);
                services.AddScoped<IEntityAuditUserIdProvider, EntityAuditUserIdProvider>();
                services.AddExtensionHostCapability<IAgentCatalogFacade>();
                services.AddExtensionHostCapability<IBackgroundExecutionFacade>();
                services.AddExtensionHostCapability<IEventPublisher>();
                services.AddExtensionHostCapability<IEventSourceFacade>();
                services.AddExtensionHostCapability<IEventScheduler>();
                services.AddExtensionHostCapability<IEventSubscriptionFacade>();
                services.AddExtensionHostCapability<IConnectionAuthorizationFacade>();
            },
            hostRole: "Standalone",
            databaseProvider,
            databaseConnectionString: connectionString,
            cancellationToken: TestContext.Current.CancellationToken
        );
        if (seedActualHostTargets)
        {
            await SeedActualHostTargetsAsync(host);
        }
        ExtensionGenerationSnapshot generation = await host.LoadAndActivateAsync(
            ExtensionId,
            resumeBackground: true,
            cancellationToken: TestContext.Current.CancellationToken
        );
        return (host, generation);
    }

    private static async Task SeedActualHostTargetsAsync(ExtensionRuntimeIntegrationHost host)
    {
        string dataDirectory = host.DataDirectory(ExtensionId);
        string dataRoot = Directory.GetParent(dataDirectory)!.Parent!.Parent!.FullName;
        string workspace = Path.Combine(dataRoot, "taskpilot-project-workspace");
        Directory.CreateDirectory(workspace);
        using IDisposable userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "TaskPilotRuntimeTest")
            )
        );
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        AgwDbContext db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        db.Agents.Add(
            new Agent
            {
                Id = AgentId,
                Name = "taskpilot-runtime-test-agent",
                DisplayName = "TaskPilot runtime test agent",
            }
        );
        db.Projects.Add(
            new Project
            {
                Id = ProjectId,
                Name = "TaskPilot runtime test project",
                Workspace = workspace,
            }
        );
        db.EventSources.Add(
            new EventSource
            {
                Id = SourceId,
                Name = "TaskPilot runtime test source",
                Kind = EventSourceKind.Webhook,
                CeSource = "/taskpilot-runtime-test/source",
                ConfigJson = "{}",
                Enabled = true,
                Version = 1,
            }
        );
        db.EventSources.AddRange(
            PublisherSource(ExecutionSourceId, "agw.execution", "/agw/executions"),
            PublisherSource(NotificationSourceId, "agw.notifications", "/agw/notifications"),
            PublisherSource(
                SchedulerSourceId,
                $"agw.scheduler:ext:{ExtensionId:D}",
                $"/agw/schedules/ext:{ExtensionId:D}"
            )
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static EventSource PublisherSource(Guid id, string publisherKey, string source) =>
        new()
        {
            Id = id,
            Name = publisherKey,
            Kind = EventSourceKind.Publisher,
            PublisherKey = publisherKey,
            CeSource = source,
            ConfigJson = "{}",
            Enabled = true,
            Version = 1,
        };

    private static string FindRepositoryRoot()
    {
        for (
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            if (File.Exists(Path.Combine(directory.FullName, "Agw.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private static TaskPilotDbContext CreateDbContext(
        ExtensionRuntimeIntegrationHost host,
        ExtensionGenerationSnapshot generation
    )
    {
        DbContextOptionsBuilder<TaskPilotDbContext> optionsBuilder = new();
        if (host.DatabaseConnection is SqliteConnection sqliteConnection)
        {
            optionsBuilder.UseSqlite(sqliteConnection);
        }
        else
        {
            optionsBuilder.UseNpgsql(host.DatabaseConnection);
        }

        DbContextOptions<TaskPilotDbContext> options = optionsBuilder.UseSnakeCaseNamingConvention().Options;
        return new TaskPilotDbContext(
            options,
            new ExtensionDatabaseIdentity(ExtensionId, generation.GenerationId.GenerationId, 1),
            new TestExtensionUserContext(UserId, Guid.CreateVersion7()),
            TimeProvider.System
        );
    }

    private static async Task<SeededRound> SeedRunningRoundAsync(
        ExtensionRuntimeIntegrationHost host,
        ExtensionGenerationSnapshot generation,
        int consecutiveFailures = 0,
        long retryRequestSeq = 0,
        IReadOnlyList<string>? emitTypes = null
    )
    {
        await using TaskPilotDbContext db = CreateDbContext(host, generation);
        TaskPilotRule rule = NewRule(emitTypes: emitTypes);
        TaskPilotMatter matter = new()
        {
            RootId = rule.Id,
            MatterKey = $"matter:{Guid.CreateVersion7():N}",
            State = TaskPilotMatterState.Active,
            ConsecutiveFailures = consecutiveFailures,
            RetryRequestSeq = retryRequestSeq,
            Version = 1,
        };
        TaskPilotRound round = NewRunningRound(matter.Id, Guid.CreateVersion7());
        db.Rules.Add(rule);
        db.RuleRevisions.Add(
            new TaskPilotRuleRevision
            {
                RootId = rule.Id,
                Version = rule.Version,
                SnapshotJson = JsonSerializer.Serialize(ToRuleDetails(rule), JsonOptions),
            }
        );
        db.Matters.Add(matter);
        db.Rounds.Add(round);
        db.RoundAttempts.Add(NewRunningAttempt(round));
        db.MatterActivities.Add(
            new TaskPilotMatterActivity
            {
                RootId = matter.Id,
                RoundId = round.Id,
                AcquiredAt = TimeProvider.System.GetUtcNow(),
            }
        );
        db.ConcurrencySlots.Add(
            new TaskPilotConcurrencySlot
            {
                GroupKey = rule.GroupKey,
                SlotIndex = 0,
                RoundId = round.Id,
            }
        );
        db.UserStates.Add(NewUserState());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new SeededRound
        {
            RuleId = rule.Id,
            MatterId = matter.Id,
            RoundId = round.Id,
            TurnId = round.TurnId!.Value,
            LaterTurnId = Guid.Empty,
        };
    }

    private static TaskPilotRule NewRule(bool paused = false, IReadOnlyList<string>? emitTypes = null) =>
        new()
        {
            Name = "Runtime test rule",
            Kind = TaskPilotRuleKind.Agent,
            Enabled = true,
            Paused = paused,
            Version = 1,
            EventTypesJson = "[\"taskpilot.input\"]",
            EmitTypesJson = JsonSerializer.Serialize(emitTypes ?? [], JsonOptions),
            TargetKind = AgentTargetKind.Agent,
            TargetId = AgentId,
            ProjectId = ProjectId,
            GroupKey = "taskpilot-test",
            MaxConcurrency = 1,
            NotifyPolicy = TaskPilotNotifyPolicy.Never,
            RetryJson = JsonSerializer.Serialize(
                new TaskPilotRetryPolicy
                {
                    DelayMinutes = [1],
                    NotificationThreshold = 100,
                    StopThreshold = 100,
                },
                JsonOptions
            ),
        };

    private static TaskPilotRuleDetails ToRuleDetails(TaskPilotRule rule) =>
        new()
        {
            Rule = new TaskPilotRuleSummary
            {
                Id = rule.Id,
                Name = rule.Name,
                Kind = rule.Kind,
                Enabled = rule.Enabled,
                Paused = rule.Paused,
                DeletedAt = rule.DeletedAt,
                Version = rule.Version,
                SubscriptionId = rule.SubscriptionId,
                SubscriptionPending = false,
                SubscriptionError = rule.SubscriptionError,
            },
            SourceIds = [],
            EventTypes = ["taskpilot.input"],
            TargetKind = rule.TargetKind,
            TargetId = rule.TargetId,
            ProjectId = rule.ProjectId,
            Instructions = "Inspect the supplied event.",
            GroupKey = rule.GroupKey,
            MaxConcurrency = rule.MaxConcurrency,
            SessionPolicy = TaskPilotSessionPolicy.PerMatter,
            EmitTypes = JsonSerializer.Deserialize<string[]>(rule.EmitTypesJson, JsonOptions) ?? [],
        };

    private static TaskPilotRound NewRunningRound(Guid matterId, Guid turnId) =>
        new()
        {
            RootId = matterId,
            RuleVersion = 1,
            State = TaskPilotRoundState.Running,
            Trigger = TaskPilotRoundTrigger.Manual,
            TurnId = turnId,
            Attempt = 1,
            Version = 1,
            HeadHops = 0,
        };

    private static TaskPilotRoundAttempt NewRunningAttempt(TaskPilotRound round) =>
        new()
        {
            RootId = round.Id,
            Attempt = round.Attempt,
            TurnId = round.TurnId!.Value,
            Purpose = TaskPilotAttemptPurpose.Run,
            State = TaskPilotAttemptState.Running,
        };

    private static TaskPilotUserState NewUserState() =>
        new() { SystemSubscriptionVersion = 1, SystemSubscriptionId = Guid.CreateVersion7() };

    private static async Task<int> CountOutboxAsync(ExtensionRuntimeIntegrationHost host, string kind)
    {
        await using DbCommand command = host.DatabaseConnection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"zext_{ExtensionId:N}___outbox\" WHERE kind = @kind";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "kind";
        parameter.Value = kind;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<(string IdempotencyKey, string PayloadJson)> FindOutboxAsync(
        ExtensionRuntimeIntegrationHost host,
        string kind,
        string? idempotencyKey = null
    )
    {
        await using DbCommand command = host.DatabaseConnection.CreateCommand();
        command.CommandText = idempotencyKey is null
            ? $"SELECT idempotency_key, payload_json FROM \"zext_{ExtensionId:N}___outbox\" WHERE kind = @kind"
            : $"SELECT idempotency_key, payload_json FROM \"zext_{ExtensionId:N}___outbox\" WHERE kind = @kind AND idempotency_key = @key";
        DbParameter kindParameter = command.CreateParameter();
        kindParameter.ParameterName = "kind";
        kindParameter.Value = kind;
        command.Parameters.Add(kindParameter);
        if (idempotencyKey is not null)
        {
            DbParameter keyParameter = command.CreateParameter();
            keyParameter.ParameterName = "key";
            keyParameter.Value = idempotencyKey;
            command.Parameters.Add(keyParameter);
        }

        await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            throw new InvalidOperationException("The expected TaskPilot outbox row was not found.");
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<string> ReadOutboxStateAsync(ExtensionRuntimeIntegrationHost host, string idempotencyKey)
    {
        await using DbCommand command = host.DatabaseConnection.CreateCommand();
        command.CommandText = $"SELECT state FROM \"zext_{ExtensionId:N}___outbox\" WHERE idempotency_key = @key";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = idempotencyKey;
        command.Parameters.Add(parameter);
        object? value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value?.ToString()
            ?? throw new InvalidOperationException("The expected TaskPilot outbox row was not found.");
    }

    private static async Task<string> ReadOutboxDiagnosticAsync(
        ExtensionRuntimeIntegrationHost host,
        string idempotencyKey
    )
    {
        await using DbCommand command = host.DatabaseConnection.CreateCommand();
        command.CommandText = $"""
            SELECT id, kind, state, attempt, last_error, result_json
            FROM "zext_{ExtensionId:N}___outbox"
            WHERE idempotency_key = @key
            """;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = idempotencyKey;
        command.Parameters.Add(parameter);
        await using DbDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return "missing";
        }

        string error = reader.IsDBNull(4) ? "null" : reader.GetString(4);
        string result = reader.IsDBNull(5) ? "null" : reader.GetString(5);
        return $"Id={reader.GetValue(0)}, Kind={reader.GetValue(1)}, State={reader.GetValue(2)}, "
            + $"Attempt={reader.GetValue(3)}, LastError={error}, Result={result}";
    }

    private static async Task<string> WaitForManagedSubscriptionErrorAsync(
        ExtensionRuntimeIntegrationHost host,
        ExtensionGenerationSnapshot generation,
        Guid ruleId,
        CancellationToken cancellationToken
    )
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(15);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            await using var db = CreateDbContext(host, generation);
            var error = await db
                .Rules.Where(rule => rule.Id == ruleId)
                .Select(rule => rule.SubscriptionError)
                .SingleAsync(cancellationToken);
            if (error is not null)
                return error;

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        throw new TimeoutException(await ReadOutboxDiagnosticAsync(host, $"rule:{ruleId:D}:subscription:1"));
    }

    private static async Task<EventSubscription> WaitForManagedSubscriptionAsync(
        ExtensionRuntimeIntegrationHost host,
        string managedKey,
        long managedVersion,
        EventSubscriptionManagedState state
    )
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow().AddSeconds(15);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
            AgwDbContext db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            EventSubscription? subscription = await db.EventSubscriptions.SingleOrDefaultAsync(
                row => row.ManagedByExtensionId == ExtensionId && row.ManagedKey == managedKey,
                TestContext.Current.CancellationToken
            );
            if (
                subscription is not null
                && subscription.ManagedVersion == managedVersion
                && subscription.ManagedState == state
                && await ReadOutboxStateAsync(host, $"{managedKey}:subscription:{managedVersion}") == "Completed"
            )
            {
                return subscription;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }

        string outbox = await ReadOutboxDiagnosticAsync(host, $"{managedKey}:subscription:{managedVersion}");
        Assert.Fail(
            $"Managed event subscription {managedKey} did not reach {state} at version {managedVersion}. "
                + $"Outbox={outbox}"
        );
        throw new InvalidOperationException("The managed event subscription did not reach its expected state.");
    }

    private static async Task WaitForRoundStateAsync(
        ExtensionRuntimeIntegrationHost host,
        ExtensionGenerationSnapshot generation,
        Guid roundId,
        TaskPilotRoundState state
    )
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow().AddSeconds(15);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            await using (TaskPilotDbContext db = CreateDbContext(host, generation))
            {
                if (
                    await db.Rounds.AnyAsync(
                        round => round.Id == roundId && round.State == state,
                        TestContext.Current.CancellationToken
                    )
                )
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }

        await using TaskPilotDbContext diagnostics = CreateDbContext(host, generation);
        TaskPilotRound? currentRound = await diagnostics.Rounds.SingleOrDefaultAsync(
            row => row.Id == roundId,
            TestContext.Current.CancellationToken
        );
        TaskPilotRoundAttempt? currentAttempt = await diagnostics.RoundAttempts.SingleOrDefaultAsync(
            row => row.RootId == roundId,
            TestContext.Current.CancellationToken
        );
        string outbox = await ReadOutboxDiagnosticAsync(host, $"taskpilot:round:{roundId:D}:attempt:1:submit");
        Assert.Fail(
            $"TaskPilot round {roundId:D} did not reach {state}. "
                + $"RoundState={currentRound?.State}, TurnId={currentRound?.TurnId}, "
                + $"AttemptId={currentAttempt?.Id}, AttemptState={currentAttempt?.State}, "
                + $"AttemptError={currentAttempt?.Error}, Outbox={outbox}"
        );
    }

    private static ExtensionEventHandlerInvocation RuleEventInvocation(Guid ruleId, CloudEventEnvelope cloudEvent) =>
        new()
        {
            HandlerId = "rule-events",
            UserId = UserId,
            Delivery = NewDelivery(cloudEvent, JsonSerializer.SerializeToElement(new { ruleId }, JsonOptions)),
        };

    private static ExtensionEventHandlerInvocation SystemEventInvocation(string userId, CloudEventEnvelope cloudEvent)
    {
        var (sourceId, source) = cloudEvent.Type switch
        {
            "agw.execution.completed" => (ExecutionSourceId, "/agw/executions"),
            "agw.notification.answered" => (NotificationSourceId, "/agw/notifications"),
            _ => (SchedulerSourceId, $"/agw/schedules/ext:{ExtensionId:D}"),
        };
        return new()
        {
            HandlerId = "system-events",
            UserId = userId,
            Delivery = NewDelivery(
                cloudEvent with
                {
                    Source = source,
                },
                JsonSerializer.SerializeToElement(new { }, JsonOptions)
            ) with
            {
                SourceId = sourceId,
            },
        };
    }

    private static ExtensionEventDelivery NewDelivery(CloudEventEnvelope cloudEvent, JsonElement handlerConfig) =>
        new()
        {
            Event = cloudEvent,
            DeliveryId = Guid.CreateVersion7(),
            Attempt = 1,
            SubscriptionId = Guid.CreateVersion7(),
            HandlerConfig = handlerConfig,
            EventId = Guid.CreateVersion7(),
            SourceId = Guid.CreateVersion7(),
            ReceivedAt = TimeProvider.System.GetUtcNow(),
            ChainId = Guid.CreateVersion7(),
            Hops = 0,
            Summary = "Runtime integration event",
        };

    private static CloudEventEnvelope NewRuleEvent(string subject) =>
        NewCloudEvent("taskpilot.input", new { value = subject }, subject);

    private static CloudEventEnvelope NewExecutionCompletedEvent(SeededRound seeded, string resultText) =>
        NewCloudEvent(
            "agw.execution.completed",
            new
            {
                caller = $"ext:{ExtensionId:D}",
                correlationKey = $"round:{seeded.RoundId:D}:1",
                turnId = seeded.TurnId,
                state = "Completed",
                resultText,
                conversationId = (Guid?)null,
                inputTokens = 11L,
                outputTokens = 7L,
            }
        );

    private static CloudEventEnvelope NewExecutionFailedEvent(Guid roundId, Guid turnId) =>
        NewCloudEvent(
            "agw.execution.completed",
            new
            {
                caller = $"ext:{ExtensionId:D}",
                correlationKey = $"round:{roundId:D}:1",
                turnId,
                state = "Failed",
                error = "temporary failure",
                resultText = (string?)null,
            }
        );

    private static CloudEventEnvelope NewRetryEvent(Guid matterId, Guid roundId, long requestSeq) =>
        NewCloudEvent(
            "taskpilot.round.retry",
            new TaskPilotRetryDueData
            {
                MatterId = matterId,
                RoundId = roundId,
                RequestSeq = requestSeq,
            },
            matterId.ToString("D")
        );

    private static CloudEventEnvelope NewCloudEvent(string type, object payload, string? subject = null)
    {
        JsonElement data = JsonSerializer.SerializeToElement(payload, JsonOptions);
        return new CloudEventEnvelope
        {
            Id = Guid.CreateVersion7().ToString("D"),
            Source = "urn:taskpilot-runtime-test",
            Type = type,
            Time = TimeProvider.System.GetUtcNow(),
            Subject = subject,
            DataContentType = "application/json",
            Data = new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = data.GetRawText(),
                Json = data,
            },
        };
    }

    private sealed record TestExtensionUserContext(string? UserId, Guid? InvocationId) : IExtensionUserContext;

    private sealed record SeededRound
    {
        public required Guid RuleId { get; init; }

        public required Guid MatterId { get; init; }

        public required Guid RoundId { get; init; }

        public required Guid TurnId { get; init; }

        public Guid? LaterRoundId { get; init; }

        public Guid LaterTurnId { get; init; }
    }
}
