using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Contracts.Execution;
using Agw.Agents.Contracts.Messages;
using Agw.Auth.Contracts;
using Agw.Events.Application;
using Agw.Events.Application.Delivery;
using Agw.Events.Application.Persistence;
using Agw.Events.Contracts;
using Agw.Events.Contracts.Transport;
using Agw.Events.Infrastructure.Dispatch;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Events.Transport;
using Agw.Notifications.Application.Persistence;
using Agw.Shared.Contracts.Conversations;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Data.Entities.Notifications;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Agw.Testing;
using Agw.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static Agw.Tests.ModuleCompositionTestHost;

namespace Agw.Events.Infrastructure.Delivery;

public sealed class EventDeliveryIntegrationTests
{
    private const string OwnerUserId = "event-delivery-integration-owner";

    [Fact]
    public async Task ProcessAsync_FailedJobDelivery_RetriesInAnotherRelatedConversation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var user = PushUser(OwnerUserId);
        await using var host = await ModuleCompositionTestHost.CreateAsync(
            settings: new Dictionary<string, string?> { ["Events:Delivery:MaxAttempts"] = "1" }
        );
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "event-agent",
            DisplayName = "Event Agent",
        };
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Name = "Event project",
            Workspace = services.GetRequiredService<AgwDataPaths>().UserHomeDirectory,
        };
        db.Agents.Add(agent);
        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);
        var job = await services
            .GetRequiredService<IEventJobFacade>()
            .CreateAsync(
                new EventJobDefinition
                {
                    Name = "Event conversation job",
                    Enabled = true,
                    ProjectId = project.Id,
                    TargetKind = EventJobTargetKind.Agent,
                    AgentId = agent.Id,
                    Instructions = "Run the scheduled work.",
                    Schedule = new EventJobSchedule
                    {
                        Kind = "Once",
                        At = TimeProvider.System.GetUtcNow().AddMinutes(-1),
                    },
                },
                cancellationToken
            );
        var scheduleId = await db.EventSchedules.Select(row => row.Id).SingleAsync(cancellationToken);
        var transport = services.GetRequiredService<IEventTransport>();

        Assert.True(await services.GetRequiredService<EventScheduleRunner>().RunAsync(scheduleId, cancellationToken));
        var accepted = await ReadTransportDeliveryAsync(transport, EventTopics.Accepted);
        await services.GetRequiredService<EventDispatcher>().DispatchAsync(accepted, cancellationToken);
        var pending = await ReadTransportDeliveryAsync(transport, EventTopics.Delivery);
        await services.GetRequiredService<EventDeliveryProcessor>().ProcessAsync(pending, cancellationToken);
        db.ChangeTracker.Clear();

        var jobKey = job.Id.ToString("D");
        var source = await db.EventSources.SingleAsync(
            row => row.ManagedBy == EventManagedBy.Job && row.ManagedKey == jobKey,
            cancellationToken
        );
        var delivery = await db.EventDeliveries.SingleAsync(cancellationToken);
        var eventId = await db
            .Events.Where(row => row.SourceId == source.Id)
            .Select(row => row.Id)
            .SingleAsync(cancellationToken);
        var conversation = await db.ProjectConversations.SingleAsync(
            row => row.ProjectId == project.Id,
            cancellationToken
        );
        Assert.Equal(EventDeliveryState.WaitingExecution, delivery.State);
        Assert.Equal(ConversationKinds.ForEventSource(source.Kind), conversation.Kind);
        Assert.Equal(eventId, conversation.RelateId);

        var firstTurnId = Assert.IsType<Guid>(delivery.TurnId);
        Assert.True(
            await services
                .GetRequiredService<IBackgroundExecutionCompletionTransaction>()
                .CompleteAsync(
                    new BackgroundExecutionCompletion
                    {
                        TurnId = firstTurnId,
                        OwnerUserId = OwnerUserId,
                        State = AgentExecutionState.Failed,
                        Error = "Execution failed.",
                        InputTokens = 0,
                        OutputTokens = 0,
                        CompletedAt = TimeProvider.System.GetUtcNow(),
                    },
                    cancellationToken
                )
        );
        var completed = await ReadTransportDeliveryAsync(transport, EventTopics.Accepted);
        await services.GetRequiredService<EventDispatcher>().DispatchAsync(completed, cancellationToken);
        Assert.True(
            await services
                .GetRequiredService<EventDeliveryManagementAppService>()
                .RetryAsync(delivery.Id, cancellationToken)
        );
        db.ChangeTracker.Clear();
        var retry = await ReadTransportDeliveryAsync(transport, EventTopics.Delivery);
        await services.GetRequiredService<EventDeliveryProcessor>().ProcessAsync(retry, cancellationToken);
        db.ChangeTracker.Clear();

        var retriedDelivery = await db.EventDeliveries.SingleAsync(cancellationToken);
        Assert.Equal(EventDeliveryState.WaitingExecution, retriedDelivery.State);
        Assert.NotEqual(firstTurnId, retriedDelivery.TurnId);
        var retryConversation = await db.ProjectConversations.SingleAsync(
            row => row.ProjectId == project.Id && row.Id != conversation.Id,
            cancellationToken
        );
        Assert.Equal(conversation.Kind, retryConversation.Kind);
        Assert.Equal(eventId, retryConversation.RelateId);
    }

    [Fact]
    public async Task ProcessAsync_RecurringJob_RunsNextOccurrenceWhileFirstExecutionIsWaiting()
    {
        using var user = PushUser(OwnerUserId);
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero));
        await using var host = await TimeControlledHost.CreateAsync(clock);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "recurring-event-agent",
            DisplayName = "Recurring Event Agent",
        };
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Name = "Recurring event project",
            Workspace = services.GetRequiredService<AgwDataPaths>().UserHomeDirectory,
        };
        db.AddRange(agent, project);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var job = await services
            .GetRequiredService<IEventJobFacade>()
            .CreateAsync(
                new EventJobDefinition
                {
                    Name = "Recurring event job",
                    Enabled = true,
                    ProjectId = project.Id,
                    TargetKind = EventJobTargetKind.Agent,
                    AgentId = agent.Id,
                    Instructions = "Run the scheduled work.",
                    Schedule = new EventJobSchedule { Kind = "Interval", Interval = "PT1M" },
                },
                TestContext.Current.CancellationToken
            );
        var scheduleId = await db
            .EventSchedules.Select(row => row.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
        var runner = services.GetRequiredService<EventScheduleRunner>();
        var dispatcher = services.GetRequiredService<EventDispatcher>();
        var processor = services.GetRequiredService<EventDeliveryProcessor>();
        var transport = services.GetRequiredService<IEventTransport>();

        async Task<EventDelivery> ExecuteOccurrenceAsync()
        {
            Assert.True(await runner.RunAsync(scheduleId, TestContext.Current.CancellationToken));
            var accepted = await ReadTransportDeliveryAsync(transport, EventTopics.Accepted);
            await dispatcher.DispatchAsync(accepted, TestContext.Current.CancellationToken);
            var pending = await ReadTransportDeliveryAsync(transport, EventTopics.Delivery);
            var deliveryId = Guid.Parse(pending.Message.Extensions["agwdeliveryid"].Value);
            await processor.ProcessAsync(pending, TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();
            return await db
                .EventDeliveries.AsNoTracking()
                .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
        }

        var firstDueAt = (
            await db
                .EventSchedules.AsNoTracking()
                .SingleAsync(row => row.Id == scheduleId, TestContext.Current.CancellationToken)
        ).DueAt;
        clock.SetUtcNow(firstDueAt!.Value);
        var first = await ExecuteOccurrenceAsync();
        Assert.Equal(EventDeliveryState.WaitingExecution, first.State);
        var nextDueAt = (
            await db
                .EventSchedules.AsNoTracking()
                .SingleAsync(row => row.Id == scheduleId, TestContext.Current.CancellationToken)
        ).DueAt;
        clock.SetUtcNow(nextDueAt!.Value);

        var second = await ExecuteOccurrenceAsync();

        var deliveries = await db
            .EventDeliveries.AsNoTracking()
            .OrderBy(row => row.Attempt)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, delivery => Assert.Equal(EventDeliveryState.WaitingExecution, delivery.State));
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.TurnId, second.TurnId);
        Assert.True(
            (
                await services
                    .GetRequiredService<IEventJobFacade>()
                    .FindAsync(job.Id, TestContext.Current.CancellationToken)
            )!
                .Definition
                .Enabled
        );
    }

    [Fact]
    public async Task RecoverDueRetriesAsync_ParksFailedJobDelivery_LeavesJobEnabledForNextTimer()
    {
        using var user = PushUser(OwnerUserId);
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero));
        await using var host = await TimeControlledHost.CreateAsync(
            clock,
            new Dictionary<string, string?> { ["Events:Delivery:MaxAttempts"] = "2" }
        );
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Name = "Retry event project",
            Workspace = services.GetRequiredService<AgwDataPaths>().UserHomeDirectory,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var job = await services
            .GetRequiredService<IEventJobFacade>()
            .CreateAsync(
                new EventJobDefinition
                {
                    Name = "Retry event job",
                    Enabled = true,
                    ProjectId = project.Id,
                    TargetKind = EventJobTargetKind.Agent,
                    AgentId = Guid.CreateVersion7(),
                    Instructions = "Run the scheduled work.",
                    Schedule = new EventJobSchedule { Kind = "Interval", Interval = "PT1M" },
                },
                TestContext.Current.CancellationToken
            );
        var scheduleId = await db
            .EventSchedules.Select(row => row.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
        var firstDueAt = (
            await db
                .EventSchedules.AsNoTracking()
                .SingleAsync(row => row.Id == scheduleId, TestContext.Current.CancellationToken)
        ).DueAt;
        clock.SetUtcNow(firstDueAt!.Value);
        var runner = services.GetRequiredService<EventScheduleRunner>();
        var dispatcher = services.GetRequiredService<EventDispatcher>();
        var processor = services.GetRequiredService<EventDeliveryProcessor>();
        var transport = services.GetRequiredService<IEventTransport>();

        Assert.True(await runner.RunAsync(scheduleId, TestContext.Current.CancellationToken));
        var accepted = await ReadTransportDeliveryAsync(transport, EventTopics.Accepted);
        await dispatcher.DispatchAsync(accepted, TestContext.Current.CancellationToken);
        var initialRetry = await ReadTransportDeliveryAsync(transport, EventTopics.Delivery);
        await processor.ProcessAsync(initialRetry, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var scheduledRetry = await db.EventDeliveries.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryState.RetryScheduled, scheduledRetry.State);
        Assert.NotNull(scheduledRetry.NextAttemptAt);
        clock.SetUtcNow(scheduledRetry.NextAttemptAt!.Value);

        Assert.Equal(
            1,
            await services
                .GetRequiredService<Agw.Infrastructure.Events.EventRecordRecovery>()
                .RecoverDueRetriesAsync(TestContext.Current.CancellationToken)
        );
        var retry = await ReadTransportDeliveryAsync(transport, EventTopics.Delivery);
        await processor.ProcessAsync(retry, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var parked = await db.EventDeliveries.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryState.Parked, parked.State);
        Assert.True(
            (
                await services
                    .GetRequiredService<IEventJobFacade>()
                    .FindAsync(job.Id, TestContext.Current.CancellationToken)
            )!
                .Definition
                .Enabled
        );

        var nextDueAt = (
            await db
                .EventSchedules.AsNoTracking()
                .SingleAsync(row => row.Id == scheduleId, TestContext.Current.CancellationToken)
        ).DueAt;
        clock.SetUtcNow(nextDueAt!.Value);
        Assert.True(await runner.RunAsync(scheduleId, TestContext.Current.CancellationToken));
        accepted = await ReadTransportDeliveryAsync(transport, EventTopics.Accepted);
        await dispatcher.DispatchAsync(accepted, TestContext.Current.CancellationToken);
        var nextTimerDelivery = await ReadTransportDeliveryAsync(transport, EventTopics.Delivery);
        await processor.ProcessAsync(nextTimerDelivery, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        Assert.Equal(
            2,
            await db.Events.CountAsync(row => row.Type == "job.timer.fired", TestContext.Current.CancellationToken)
        );
        Assert.Equal(2, await db.EventDeliveries.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            EventScheduleState.Active,
            (
                await db
                    .EventSchedules.AsNoTracking()
                    .SingleAsync(row => row.Id == scheduleId, TestContext.Current.CancellationToken)
            ).State
        );
    }

    [Fact]
    public async Task DeleteAsync_JobWithWaitingRetryWithoutRequest_CancelsRetry()
    {
        using var user = PushUser(OwnerUserId);
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero));
        await using var host = await TimeControlledHost.CreateAsync(clock);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Name = "Waiting retry project",
            Workspace = services.GetRequiredService<AgwDataPaths>().UserHomeDirectory,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var job = await services
            .GetRequiredService<IEventJobFacade>()
            .CreateAsync(
                new EventJobDefinition
                {
                    Name = "Waiting retry job",
                    Enabled = true,
                    ProjectId = project.Id,
                    TargetKind = EventJobTargetKind.Agent,
                    AgentId = Guid.CreateVersion7(),
                    Instructions = "Run the scheduled work.",
                    Schedule = new EventJobSchedule { Kind = "Once", At = clock.GetUtcNow().AddMinutes(-1) },
                },
                TestContext.Current.CancellationToken
            );
        var scheduleId = await db
            .EventSchedules.Select(row => row.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(
            await services
                .GetRequiredService<EventScheduleRunner>()
                .RunAsync(scheduleId, TestContext.Current.CancellationToken)
        );
        var transport = services.GetRequiredService<IEventTransport>();
        var accepted = await ReadTransportDeliveryAsync(transport, EventTopics.Accepted);
        await services
            .GetRequiredService<EventDispatcher>()
            .DispatchAsync(accepted, TestContext.Current.CancellationToken);
        var delivery = await db.EventDeliveries.SingleAsync(TestContext.Current.CancellationToken);
        delivery.State = EventDeliveryState.RetryScheduled;
        delivery.Attempt = 1;
        delivery.NextAttemptAt = clock.GetUtcNow().AddSeconds(-1);
        delivery.TurnId = null;
        delivery.RequestSnapshotJson = null;
        delivery.Version++;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        await services.GetRequiredService<IEventJobFacade>().DeleteAsync(job.Id, TestContext.Current.CancellationToken);
        Assert.Equal(
            1,
            await services
                .GetRequiredService<Agw.Infrastructure.Events.EventRecordRecovery>()
                .RecoverDueRetriesAsync(TestContext.Current.CancellationToken)
        );
        var retry = await ReadTransportDeliveryAsync(transport, EventTopics.Delivery);
        await services
            .GetRequiredService<EventDeliveryProcessor>()
            .ProcessAsync(retry, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var cancelled = await db.EventDeliveries.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryState.Cancelled, cancelled.State);
        Assert.Null(cancelled.TurnId);
        Assert.Null(cancelled.RequestSnapshotJson);
    }

    [Fact]
    public async Task DeleteAsync_JobWithSubmittedExecution_AllowsExecutionToFinish()
    {
        using var user = PushUser(OwnerUserId);
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero));
        await using var host = await TimeControlledHost.CreateAsync(clock);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "submitted-event-agent",
            DisplayName = "Submitted Event Agent",
        };
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Name = "Submitted event project",
            Workspace = services.GetRequiredService<AgwDataPaths>().UserHomeDirectory,
        };
        db.AddRange(agent, project);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var job = await services
            .GetRequiredService<IEventJobFacade>()
            .CreateAsync(
                new EventJobDefinition
                {
                    Name = "Submitted execution job",
                    Enabled = true,
                    ProjectId = project.Id,
                    TargetKind = EventJobTargetKind.Agent,
                    AgentId = agent.Id,
                    Instructions = "Run the scheduled work.",
                    Schedule = new EventJobSchedule { Kind = "Once", At = clock.GetUtcNow().AddMinutes(-1) },
                },
                TestContext.Current.CancellationToken
            );
        var scheduleId = await db
            .EventSchedules.Select(row => row.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(
            await services
                .GetRequiredService<EventScheduleRunner>()
                .RunAsync(scheduleId, TestContext.Current.CancellationToken)
        );
        var transport = services.GetRequiredService<IEventTransport>();
        var accepted = await ReadTransportDeliveryAsync(transport, EventTopics.Accepted);
        await services
            .GetRequiredService<EventDispatcher>()
            .DispatchAsync(accepted, TestContext.Current.CancellationToken);
        var pending = await ReadTransportDeliveryAsync(transport, EventTopics.Delivery);
        await services
            .GetRequiredService<EventDeliveryProcessor>()
            .ProcessAsync(pending, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var delivery = await db.EventDeliveries.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryState.WaitingExecution, delivery.State);
        var turnId = Assert.IsType<Guid>(delivery.TurnId);

        await services.GetRequiredService<IEventJobFacade>().DeleteAsync(job.Id, TestContext.Current.CancellationToken);
        Assert.Equal(
            EventDeliveryState.WaitingExecution,
            (await db.EventDeliveries.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).State
        );
        Assert.True(
            await services
                .GetRequiredService<IBackgroundExecutionCompletionTransaction>()
                .CompleteAsync(
                    new BackgroundExecutionCompletion
                    {
                        TurnId = turnId,
                        OwnerUserId = OwnerUserId,
                        State = AgentExecutionState.Completed,
                        Error = null,
                        InputTokens = 0,
                        OutputTokens = 0,
                        CompletedAt = clock.GetUtcNow(),
                    },
                    TestContext.Current.CancellationToken
                )
        );
        var completed = await ReadTransportDeliveryAsync(transport, EventTopics.Accepted);
        await services
            .GetRequiredService<EventDispatcher>()
            .DispatchAsync(completed, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var finished = await db.EventDeliveries.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryState.Succeeded, finished.State);
        Assert.Equal(
            EventDeliveryAttemptResult.Succeeded,
            (await db.EventDeliveryAttempts.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Result
        );
    }

    [Theory]
    [InlineData("""{"===":[{"var":"data"},{"state":"open"}]}""", "Cannot identify rule for state")]
    [InlineData("""{"===":[{"var":"type"}]}""", "needs an array with 2 parameters")]
    public async Task SaveSubscriptionAsync_InvalidFilter_ReturnsRestrictionAndReason(string filter, string reason)
    {
        using var user = PushUser(OwnerUserId);
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        using var document = JsonDocument.Parse(filter);
        var request = new EventSubscriptionRequest
        {
            Name = "invalid-filter",
            EventTypes = ["example.updated"],
            TargetKind = EventSubscriptionTargetKind.Agent,
            Filter = document.RootElement,
        };
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = scope.ServiceProvider.GetRequiredService<EventManagementAppService>();

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            service.SaveSubscriptionAsync(request, create: true, cancellationToken)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Contains("Single-key object literals are not supported", error.Message);
        Assert.Contains(reason, error.Message);
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        Assert.False(await db.EventSubscriptions.AnyAsync(cancellationToken));
    }

    [Fact]
    public async Task ProcessAsync_UnavailableExtension_HoldsDeliveryWithoutAttempt()
    {
        using var user = PushUser(OwnerUserId);
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var eventId = await StageEventAsync(services, "example.updated", "ext:019a4c17-0000-7000-8000-000000000001");
        var deliveryId = Guid.CreateVersion7();
        var subscriptionId = Guid.CreateVersion7();
        db.EventSubscriptions.Add(
            new EventSubscription
            {
                Id = subscriptionId,
                CreateBy = OwnerUserId,
                Name = "unavailable-handler",
                Enabled = true,
                TargetKind = EventSubscriptionTargetKind.ExtensionHandler,
                HandlerExtensionId = Guid.CreateVersion7(),
                HandlerId = "handler",
            }
        );
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                CreateBy = OwnerUserId,
                EventId = eventId,
                SubscriptionId = subscriptionId,
                State = EventDeliveryState.Pending,
                RecoverAfter = TimeProvider.System.GetUtcNow(),
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var eventRecord = await db.Events.SingleAsync(row => row.Id == eventId, TestContext.Current.CancellationToken);
        var transportDelivery = new TransportDelivery
        {
            Topic = "agw.events.delivery",
            Message = EventDeliveryMessageFactory.Create(eventRecord, deliveryId),
            MessageId = Guid.CreateVersion7().ToString("D"),
            DeliveryCount = 1,
        };

        db.ChangeTracker.Clear();
        await services
            .GetRequiredService<EventDeliveryProcessor>()
            .ProcessAsync(transportDelivery, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var delivery = await db
            .EventDeliveries.AsNoTracking()
            .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryState.Held, delivery.State);
        Assert.Equal(0, delivery.Attempt);
        Assert.Empty(
            await db
                .EventDeliveryAttempts.AsNoTracking()
                .Where(row => row.DeliveryId == deliveryId)
                .ToArrayAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal(
            0,
            await services
                .GetRequiredService<EventDeliveryProcessor>()
                .RequeueHeldAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task RetryAsync_ParkedDelivery_RequeuesAndPublishesDelivery()
    {
        using var user = PushUser(OwnerUserId);
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var eventId = await StageEventAsync(services, "example.updated", "ext:019a4c17-0000-7000-8000-000000000001");
        var deliveryId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var requestSnapshot = "persisted request snapshot";
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                CreateBy = OwnerUserId,
                EventId = eventId,
                SubscriptionId = Guid.CreateVersion7(),
                State = EventDeliveryState.Parked,
                Attempt = 3,
                TurnId = turnId,
                RequestSnapshotJson = requestSnapshot,
                RecoverAfter = TimeProvider.System.GetUtcNow(),
                LastError = "attempts exhausted",
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(
            await services
                .GetRequiredService<EventDeliveryManagementAppService>()
                .RetryAsync(deliveryId, TestContext.Current.CancellationToken)
        );
        db.ChangeTracker.Clear();

        var delivery = await db
            .EventDeliveries.AsNoTracking()
            .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryState.Pending, delivery.State);
        Assert.Null(delivery.NextAttemptAt);
        Assert.Null(delivery.LastError);
        Assert.Equal(turnId, delivery.TurnId);
        Assert.Equal(requestSnapshot, delivery.RequestSnapshotJson);
        var transportDelivery = await ReadTransportDeliveryAsync(
            services.GetRequiredService<IEventTransport>(),
            "agw.events.delivery"
        );
        Assert.Equal(deliveryId.ToString(), transportDelivery.Message.Extensions["agwdeliveryid"].Value);
    }

    [Fact]
    public async Task CancelAsync_WaitingExecution_RegistersDeliveryOwnedCancellation()
    {
        using var user = PushUser(OwnerUserId);
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var eventId = await StageEventAsync(services, "example.updated", "ext:019a4c17-0000-7000-8000-000000000001");
        var deliveryId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var startedAt = TimeProvider.System.GetUtcNow().AddMinutes(-1);
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                CreateBy = OwnerUserId,
                EventId = eventId,
                SubscriptionId = Guid.CreateVersion7(),
                State = EventDeliveryState.WaitingExecution,
                Attempt = 1,
                TurnId = turnId,
                RecoverAfter = TimeProvider.System.GetUtcNow(),
            }
        );
        db.EventDeliveryAttempts.Add(
            new EventDeliveryAttempt
            {
                Id = Guid.CreateVersion7(),
                DeliveryId = deliveryId,
                Attempt = 1,
                TurnId = turnId,
                StartedAt = startedAt,
                EndedAt = startedAt,
                Result = EventDeliveryAttemptResult.Retry,
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(
            await services
                .GetRequiredService<EventDeliveryManagementAppService>()
                .CancelAsync(deliveryId, TestContext.Current.CancellationToken)
        );
        db.ChangeTracker.Clear();

        var delivery = await db
            .EventDeliveries.AsNoTracking()
            .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
        var execution = await db
            .BackgroundExecutions.AsNoTracking()
            .SingleAsync(row => row.Id == turnId, TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryState.Cancelled, delivery.State);
        Assert.Equal(AgentExecutionState.Interrupted, execution.State);
        Assert.Equal($"delivery:{deliveryId:D}", execution.CallerKey);
        Assert.Equal(eventId, execution.ParentEventId);
        var attempt = await db
            .EventDeliveryAttempts.AsNoTracking()
            .SingleAsync(row => row.DeliveryId == deliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryAttemptResult.Failed, attempt.Result);
        Assert.Equal("The event delivery was cancelled.", attempt.Error);
        Assert.True(attempt.EndedAt > attempt.StartedAt);
        Assert.Equal(turnId, attempt.TurnId);
    }

    [Theory]
    [InlineData("Completed", null, EventDeliveryState.Succeeded, EventDeliveryAttemptResult.Succeeded)]
    [InlineData("Failed", "execution failed", EventDeliveryState.RetryScheduled, EventDeliveryAttemptResult.Retry)]
    public async Task DispatchAsync_BackgroundCompletion_UpdatesDeliveryOnce(
        string executionState,
        string? error,
        EventDeliveryState expectedState,
        EventDeliveryAttemptResult expectedAttemptResult
    )
    {
        using var user = PushUser(OwnerUserId);
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var deliveryId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var startedAt = TimeProvider.System.GetUtcNow();
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                CreateBy = OwnerUserId,
                EventId = Guid.CreateVersion7(),
                SubscriptionId = Guid.CreateVersion7(),
                State = EventDeliveryState.WaitingExecution,
                Attempt = 1,
                TurnId = turnId,
                RecoverAfter = startedAt,
            }
        );
        db.EventDeliveryAttempts.Add(
            new EventDeliveryAttempt
            {
                Id = Guid.CreateVersion7(),
                DeliveryId = deliveryId,
                Attempt = 1,
                HostId = "events-test-host",
                StartedAt = startedAt,
                EndedAt = startedAt,
                Result = EventDeliveryAttemptResult.Retry,
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var data = JsonSerializer.SerializeToElement(
            new
            {
                turnId,
                caller = $"delivery:{deliveryId:D}",
                correlationKey = $"delivery:{deliveryId:D}",
                state = executionState,
                error,
            }
        );
        await StageEventAsync(services, "agw.execution.completed", "agw.execution", data, "/agw/executions");
        var acceptedEvent = await ReadTransportDeliveryAsync(
            services.GetRequiredService<IEventTransport>(),
            "agw.events.accepted"
        );
        var dispatcher = services.GetRequiredService<EventDispatcher>();

        await dispatcher.DispatchAsync(acceptedEvent, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var deliveryAfterDispatch = await db
            .EventDeliveries.AsNoTracking()
            .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
        var attemptAfterDispatch = await db
            .EventDeliveryAttempts.AsNoTracking()
            .SingleAsync(row => row.DeliveryId == deliveryId, TestContext.Current.CancellationToken);
        var deliveryVersion = deliveryAfterDispatch.Version;

        await dispatcher.DispatchAsync(acceptedEvent, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        Assert.Equal(expectedState, deliveryAfterDispatch.State);
        Assert.Equal(expectedAttemptResult, attemptAfterDispatch.Result);
        Assert.Equal(
            deliveryVersion,
            (
                await db
                    .EventDeliveries.AsNoTracking()
                    .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken)
            ).Version
        );
        Assert.NotNull(services.GetRequiredService<IEventExecutionCompletionHandler>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverDueRetriesAsync_DueRetryScheduledDelivery_RequeuesAndPublishes(bool outboxEnabled)
    {
        using var user = PushUser(OwnerUserId);
        await using var host = await ModuleCompositionTestHost.CreateAsync(outboxEnabled);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var eventId = await StageEventAsync(services, "example.updated", "ext:019a4c17-0000-7000-8000-000000000001");
        var now = TimeProvider.System.GetUtcNow();
        var record = await db.Events.SingleAsync(row => row.Id == eventId, TestContext.Current.CancellationToken);
        record.DispatchState = EventDispatchState.Dispatched;
        if (outboxEnabled)
        {
            var acceptedOutbox = await db.EventOutbox.SingleAsync(
                row => row.Topic == "agw.events.accepted" && row.EntityId == eventId,
                TestContext.Current.CancellationToken
            );
            acceptedOutbox.State = EventOutboxState.Completed;
            acceptedOutbox.CompletedAt = now;
        }

        var deliveryId = Guid.CreateVersion7();
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                CreateBy = OwnerUserId,
                EventId = eventId,
                SubscriptionId = Guid.CreateVersion7(),
                State = EventDeliveryState.RetryScheduled,
                Attempt = 1,
                NextAttemptAt = now.AddSeconds(-1),
                RecoverAfter = now.AddSeconds(-1),
                LastError = "previous attempt failed",
            }
        );
        db.EventDeliveryAttempts.Add(
            new EventDeliveryAttempt
            {
                Id = Guid.CreateVersion7(),
                DeliveryId = deliveryId,
                Attempt = 1,
                HostId = "events-test-host",
                StartedAt = now.AddMinutes(-1),
                EndedAt = now.AddMinutes(-1),
                Result = EventDeliveryAttemptResult.Retry,
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await services
            .GetRequiredService<Agw.Infrastructure.Events.EventRecordRecovery>()
            .RecoverDueRetriesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var delivery = await db
            .EventDeliveries.AsNoTracking()
            .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryState.Pending, delivery.State);
        Assert.Null(delivery.NextAttemptAt);
        Assert.Null(delivery.LastError);
        Assert.True(delivery.RecoverAfter > now);

        var transportDelivery = await ReadTransportDeliveryAsync(
            services.GetRequiredService<IEventTransport>(),
            "agw.events.delivery"
        );
        Assert.Equal(record.CeId, transportDelivery.Message.Id);
        Assert.Equal(record.CeSource, transportDelivery.Message.Source);
        Assert.Equal(deliveryId.ToString("D"), transportDelivery.Message.Extensions["agwdeliveryid"].Value);
    }

    [Theory]
    [InlineData(false, 1, EventDeliveryState.RetryScheduled)]
    [InlineData(false, 3, EventDeliveryState.Parked)]
    [InlineData(true, 1, EventDeliveryState.RetryScheduled)]
    [InlineData(true, 3, EventDeliveryState.Parked)]
    public async Task RecoverAsync_ExpiredRunningDeliveryRetriesOrParks(
        bool outboxEnabled,
        int attemptNumber,
        EventDeliveryState expectedState
    )
    {
        using var user = PushUser(OwnerUserId);
        await using var host = await ModuleCompositionTestHost.CreateAsync(outboxEnabled);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var eventId = await StageEventAsync(services, "example.updated", "ext:019a4c17-0000-7000-8000-000000000001");
        var now = TimeProvider.System.GetUtcNow();
        var record = await db.Events.SingleAsync(row => row.Id == eventId, TestContext.Current.CancellationToken);
        record.DispatchState = EventDispatchState.Dispatched;
        if (outboxEnabled)
        {
            var acceptedOutbox = await db.EventOutbox.SingleAsync(
                row => row.Topic == "agw.events.accepted" && row.EntityId == eventId,
                TestContext.Current.CancellationToken
            );
            acceptedOutbox.State = EventOutboxState.Completed;
            acceptedOutbox.CompletedAt = now;
        }

        var deliveryId = Guid.CreateVersion7();
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                CreateBy = OwnerUserId,
                EventId = eventId,
                SubscriptionId = Guid.CreateVersion7(),
                State = EventDeliveryState.Running,
                Attempt = attemptNumber,
                RecoverAfter = now.AddSeconds(-1),
            }
        );
        db.EventDeliveryAttempts.Add(
            new EventDeliveryAttempt
            {
                Id = Guid.CreateVersion7(),
                DeliveryId = deliveryId,
                Attempt = attemptNumber,
                HostId = "events-test-host",
                StartedAt = now.AddMinutes(-1),
                EndedAt = now.AddMinutes(-1),
                Result = EventDeliveryAttemptResult.Retry,
            }
        );
        if (outboxEnabled)
        {
            db.EventOutbox.Add(
                new EventOutbox
                {
                    Id = Guid.CreateVersion7(),
                    Topic = "agw.events.delivery",
                    EntityId = deliveryId,
                    State = EventOutboxState.Published,
                    PublishedAt = now.AddMinutes(-1),
                }
            );
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await services
            .GetRequiredService<Agw.Infrastructure.Events.EventRecordRecovery>()
            .RecoverAsync(false, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var delivery = await db
            .EventDeliveries.AsNoTracking()
            .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
        var attempt = await db
            .EventDeliveryAttempts.AsNoTracking()
            .SingleAsync(row => row.DeliveryId == deliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(expectedState, delivery.State);
        Assert.Equal(
            expectedState == EventDeliveryState.Parked
                ? EventDeliveryAttemptResult.Failed
                : EventDeliveryAttemptResult.Retry,
            attempt.Result
        );
        Assert.True(attempt.EndedAt > now.AddSeconds(-1));
        Assert.Equal(expectedState == EventDeliveryState.RetryScheduled, delivery.NextAttemptAt.HasValue);
        if (outboxEnabled)
        {
            Assert.Equal(
                EventOutboxState.Completed,
                (
                    await db.EventOutbox.SingleAsync(
                        row => row.EntityId == deliveryId,
                        TestContext.Current.CancellationToken
                    )
                ).State
            );
        }
    }

    [Fact]
    public async Task RecoverAsync_RestartAfterInitialRedisPublishFailure_ReplaysPendingDeliveryAndNotification()
    {
        var redis = new RedisServerFixture();
        await redis.InitializeAsync();
        var redisEndpoint = Assert.IsType<IPEndPoint>(Assert.Single(redis.Connection.GetEndPoints()));
        var liveRedisConnection = $"{redisEndpoint.Address}:{redisEndpoint.Port},connectTimeout=500,syncTimeout=500";
        const string unavailableRedisConnection =
            "127.0.0.1:1,abortConnect=false,connectTimeout=500,syncTimeout=500,connectRetry=0";
        var testRoot = Path.Combine(AppContext.BaseDirectory, $"event-delivery-restart-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(testRoot);
        try
        {
            foreach (var outboxEnabled in new[] { false, true })
            {
                var root = Path.Combine(testRoot, $"outbox-{outboxEnabled}");
                Directory.CreateDirectory(root);
                var staleAfter = TimeSpan.FromMilliseconds(50);
                var deliveryId = Guid.CreateVersion7();
                var dispatchId = Guid.CreateVersion7();
                string eventIdText;
                string eventSource;
                await using (
                    var firstHost = await ModuleCompositionTestHost.CreateAsync(
                        outboxEnabled,
                        root,
                        "Redis",
                        unavailableRedisConnection,
                        staleAfter
                    )
                )
                {
                    using var user = PushUser(OwnerUserId);
                    await using var scope = firstHost.Services.CreateAsyncScope();
                    var services = scope.ServiceProvider;
                    var db = services.GetRequiredService<AgwDbContext>();
                    var eventId = await StageEventAsync(
                        services,
                        "example.updated",
                        "ext:019a4c17-0000-7000-8000-000000000001"
                    );
                    var record = await db.Events.SingleAsync(
                        row => row.Id == eventId,
                        TestContext.Current.CancellationToken
                    );
                    record.DispatchState = EventDispatchState.Dispatched;
                    if (outboxEnabled)
                    {
                        var acceptedOutbox = await db.EventOutbox.SingleAsync(
                            row => row.Topic == EventTopics.Accepted && row.EntityId == eventId,
                            TestContext.Current.CancellationToken
                        );
                        acceptedOutbox.State = EventOutboxState.Completed;
                        acceptedOutbox.CompletedAt = TimeProvider.System.GetUtcNow();
                    }

                    var now = TimeProvider.System.GetUtcNow();
                    eventIdText = record.CeId;
                    eventSource = record.CeSource;
                    db.EventDeliveries.Add(
                        new EventDelivery
                        {
                            Id = deliveryId,
                            CreateBy = OwnerUserId,
                            EventId = eventId,
                            SubscriptionId = Guid.CreateVersion7(),
                            State = EventDeliveryState.Pending,
                            RecoverAfter = outboxEnabled ? now.Add(staleAfter) : now.AddMilliseconds(-1),
                        }
                    );
                    services
                        .GetRequiredService<IOutboxStaging>()
                        .Stage(
                            new OutboxMessage
                            {
                                Topic = EventTopics.Delivery,
                                EntityId = deliveryId,
                                Message = EventDeliveryMessageFactory.Create(record, deliveryId),
                            }
                        );
                    await db.SaveChangesAsync(TestContext.Current.CancellationToken);

                    var notification = new Notification
                    {
                        Id = Guid.CreateVersion7(),
                        CreateBy = OwnerUserId,
                        Kind = NotificationKind.Notice,
                        Title = "Recovery test",
                        Text = "Notification after restart.",
                        CallerKey = "agw.events",
                        IdempotencyKey = "redis-restart-notification",
                        CorrelationKey = "redis-restart-notification",
                        ChainId = Guid.CreateVersion7(),
                        State = NotificationState.Open,
                        ChangedAt = now,
                        Version = 1,
                    };
                    var dispatch = new NotificationDispatch
                    {
                        Id = dispatchId,
                        NotificationId = notification.Id,
                        ChannelKey = $"ext:{Guid.CreateVersion7():D}:cards:target-1",
                        State = NotificationDispatchState.Pending,
                        MessageVersion = 1,
                        RecoverAfter = now.Add(staleAfter),
                    };
                    await services
                        .GetRequiredService<INotificationTransaction>()
                        .CreateAsync(notification, question: null, [dispatch], TestContext.Current.CancellationToken);

                    var initialRecovery = services.GetRequiredService<Agw.Infrastructure.Events.EventRecordRecovery>();
                    if (outboxEnabled)
                        await initialRecovery.RecoverDueRetriesAsync(TestContext.Current.CancellationToken);
                    else
                        await initialRecovery.RecoverAsync(false, TestContext.Current.CancellationToken);

                    db.ChangeTracker.Clear();
                    var storedDelivery = await db
                        .EventDeliveries.AsNoTracking()
                        .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
                    Assert.Equal(EventDeliveryState.Pending, storedDelivery.State);
                    Assert.Equal(
                        NotificationDispatchState.Pending,
                        (
                            await db
                                .NotificationDispatches.AsNoTracking()
                                .SingleAsync(row => row.Id == dispatchId, TestContext.Current.CancellationToken)
                        ).State
                    );
                    if (outboxEnabled)
                    {
                        Assert.Equal(
                            EventOutboxState.Pending,
                            (
                                await db
                                    .EventOutbox.AsNoTracking()
                                    .SingleAsync(
                                        row => row.Topic == EventTopics.Delivery && row.EntityId == deliveryId,
                                        TestContext.Current.CancellationToken
                                    )
                            ).State
                        );
                    }
                }

                await Task.Delay(staleAfter + TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
                await using var restartedHost = await ModuleCompositionTestHost.CreateAsync(
                    outboxEnabled,
                    root,
                    "Redis",
                    liveRedisConnection,
                    staleAfter
                );
                using var restartedUser = PushUser(OwnerUserId);
                await using var restartedScope = restartedHost.Services.CreateAsyncScope();
                var restartedServices = restartedScope.ServiceProvider;
                var recovery = restartedServices.GetRequiredService<Agw.Infrastructure.Events.EventRecordRecovery>();
                if (outboxEnabled)
                    await recovery.RecoverDueRetriesAsync(TestContext.Current.CancellationToken);
                else
                    await recovery.RecoverAsync(false, TestContext.Current.CancellationToken);

                var transportDelivery = await ReadTransportDeliveryAsync(
                    restartedServices.GetRequiredService<IEventTransport>(),
                    EventTopics.Delivery
                );
                Assert.Equal(eventIdText, transportDelivery.Message.Id);
                Assert.Equal(eventSource, transportDelivery.Message.Source);
                Assert.Equal(deliveryId.ToString("D"), transportDelivery.Message.Extensions["agwdeliveryid"].Value);
                var notificationDelivery = await ReadTransportDeliveryAsync(
                    restartedServices.GetRequiredService<IEventTransport>(),
                    EventTopics.NotificationDispatch
                );
                Assert.Equal($"{dispatchId:D}:1", notificationDelivery.Message.Id);
            }
        }
        finally
        {
            await redis.DisposeAsync();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("id")]
    public async Task ProcessAsync_DeliveryEnvelopeIdentityMismatch_LeavesDeliveryPending(string mismatch)
    {
        using var user = PushUser(OwnerUserId);
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var eventId = await StageEventAsync(services, "example.updated", "ext:019a4c17-0000-7000-8000-000000000001");
        var deliveryId = Guid.CreateVersion7();
        var subscriptionId = Guid.CreateVersion7();
        db.EventSubscriptions.Add(
            new EventSubscription
            {
                Id = subscriptionId,
                CreateBy = OwnerUserId,
                Name = "unavailable-handler",
                Enabled = true,
                TargetKind = EventSubscriptionTargetKind.ExtensionHandler,
                HandlerExtensionId = Guid.CreateVersion7(),
                HandlerId = "handler",
            }
        );
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                CreateBy = OwnerUserId,
                EventId = eventId,
                SubscriptionId = subscriptionId,
                State = EventDeliveryState.Pending,
                RecoverAfter = TimeProvider.System.GetUtcNow(),
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var record = await db.Events.SingleAsync(row => row.Id == eventId, TestContext.Current.CancellationToken);
        var message = EventDeliveryMessageFactory.Create(record, deliveryId);
        var transportDelivery = new TransportDelivery
        {
            Topic = "agw.events.delivery",
            Message =
                mismatch == "source" ? message with { Source = "/wrong/source" } : message with { Id = "wrong-id" },
            MessageId = Guid.CreateVersion7().ToString("D"),
            DeliveryCount = 1,
        };

        db.ChangeTracker.Clear();
        await services
            .GetRequiredService<EventDeliveryProcessor>()
            .ProcessAsync(transportDelivery, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var delivery = await db
            .EventDeliveries.AsNoTracking()
            .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryState.Pending, delivery.State);
        Assert.Equal(0, delivery.Attempt);
    }

    [Fact]
    public async Task ProcessAsync_AmbiguousAgentRetry_ReusesTurnIdAndOriginalRequestSnapshot()
    {
        using var user = PushUser(OwnerUserId);
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero));
        await using var host = await TimeControlledHost.CreateAsync(clock);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "retry-event-agent",
            DisplayName = "Retry Event Agent",
        };
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Name = "Retry event project",
            Workspace = services.GetRequiredService<AgwDataPaths>().UserHomeDirectory,
        };
        db.AddRange(agent, project);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var eventId = await StageEventAsync(services, "example.updated", "ext:019a4c17-0000-7000-8000-000000000001");
        var deliveryId = Guid.CreateVersion7();
        var subscriptionId = Guid.CreateVersion7();
        db.EventSubscriptions.Add(
            new EventSubscription
            {
                Id = subscriptionId,
                CreateBy = OwnerUserId,
                Name = "original name",
                Enabled = true,
                TargetKind = EventSubscriptionTargetKind.Agent,
                AgentId = agent.Id,
                ProjectId = project.Id,
                Instructions = "original instructions",
                PermissionMode = 0,
            }
        );
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                CreateBy = OwnerUserId,
                EventId = eventId,
                SubscriptionId = subscriptionId,
                State = EventDeliveryState.Pending,
                RecoverAfter = clock.GetUtcNow(),
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var record = await db.Events.SingleAsync(row => row.Id == eventId, TestContext.Current.CancellationToken);
        var firstMessage = EventDeliveryMessageFactory.Create(record, deliveryId) with
        {
            Type = "transport.first",
            DataContentType = "application/json",
            Data = JsonData("{\"value\":\"first\"}"),
        };
        db.ChangeTracker.Clear();
        await services
            .GetRequiredService<EventDeliveryProcessor>()
            .ProcessAsync(
                new TransportDelivery
                {
                    Topic = "agw.events.delivery",
                    Message = firstMessage,
                    MessageId = Guid.CreateVersion7().ToString("D"),
                    DeliveryCount = 1,
                },
                TestContext.Current.CancellationToken
            );
        db.ChangeTracker.Clear();

        var firstAttempt = await db
            .EventDeliveries.AsNoTracking()
            .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryState.WaitingExecution, firstAttempt.State);
        var turnId = Assert.IsType<Guid>(firstAttempt.TurnId);
        var requestSnapshot = Assert.IsType<string>(firstAttempt.RequestSnapshotJson);
        var firstConversation = await db
            .ProjectConversations.AsNoTracking()
            .SingleAsync(row => row.ProjectId == project.Id, TestContext.Current.CancellationToken);
        var encryptedSnapshot = await db
            .Database.SqlQueryRaw<string>(
                "SELECT request_snapshot_json AS Value FROM event_delivery WHERE id = {0}",
                deliveryId
            )
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(requestSnapshot, encryptedSnapshot);
        Assert.DoesNotContain("original instructions", encryptedSnapshot);
        var originalRequest = Assert.IsType<BackgroundExecutionRequest>(
            JsonUtil.Deserialize<BackgroundExecutionRequest>(requestSnapshot)
        );
        Assert.Equal("original instructions", Assert.IsType<AgwTextContent>(originalRequest.Input.Contents[0]).Content);
        var originalReference = Assert.IsType<AgwTextContent>(originalRequest.Input.Contents[1]).Content;
        Assert.Contains("transport.first", originalReference);
        Assert.Contains("first", originalReference);

        var subscription = await db.EventSubscriptions.SingleAsync(
            row => row.Id == subscriptionId,
            TestContext.Current.CancellationToken
        );
        subscription.Name = "edited name";
        subscription.Instructions = "edited instructions";
        var retry = await db.EventDeliveries.SingleAsync(
            row => row.Id == deliveryId,
            TestContext.Current.CancellationToken
        );
        retry.State = EventDeliveryState.Pending;
        retry.NextAttemptAt = null;
        retry.RecoverAfter = clock.GetUtcNow();
        retry.Version++;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var secondMessage = firstMessage with { Type = "transport.second", Data = JsonData("{\"value\":\"second\"}") };
        db.ChangeTracker.Clear();

        await services
            .GetRequiredService<EventDeliveryProcessor>()
            .ProcessAsync(
                new TransportDelivery
                {
                    Topic = "agw.events.delivery",
                    Message = secondMessage,
                    MessageId = Guid.CreateVersion7().ToString("D"),
                    DeliveryCount = 2,
                },
                TestContext.Current.CancellationToken
            );
        db.ChangeTracker.Clear();

        var retriedDelivery = await db
            .EventDeliveries.AsNoTracking()
            .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(turnId, retriedDelivery.TurnId);
        Assert.Equal(requestSnapshot, retriedDelivery.RequestSnapshotJson);
        Assert.Equal(EventDeliveryState.WaitingExecution, retriedDelivery.State);
        var retriedConversation = await db
            .ProjectConversations.AsNoTracking()
            .SingleAsync(row => row.ProjectId == project.Id, TestContext.Current.CancellationToken);
        Assert.Equal(firstConversation.Id, retriedConversation.Id);
        var attempts = await db
            .EventDeliveryAttempts.AsNoTracking()
            .Where(row => row.DeliveryId == deliveryId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, attempt => Assert.Equal(turnId, attempt.TurnId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_InvalidAgentRequestPreparation_UsesBoundedAttempt(bool invalidSnapshot)
    {
        using var user = PushUser(OwnerUserId);
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var eventId = await StageEventAsync(services, "example.updated", "ext:019a4c17-0000-7000-8000-000000000001");
        var deliveryId = Guid.CreateVersion7();
        var subscriptionId = Guid.CreateVersion7();
        var turnId = invalidSnapshot ? Guid.CreateVersion7() : (Guid?)null;
        db.EventSubscriptions.Add(
            new EventSubscription
            {
                Id = subscriptionId,
                CreateBy = OwnerUserId,
                Name = "invalid-agent-subscription",
                Enabled = true,
                TargetKind = EventSubscriptionTargetKind.Agent,
                AgentId = Guid.CreateVersion7(),
                ProjectId = invalidSnapshot ? Guid.CreateVersion7() : null,
                PermissionMode = 0,
            }
        );
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                CreateBy = OwnerUserId,
                EventId = eventId,
                SubscriptionId = subscriptionId,
                State = EventDeliveryState.Pending,
                TurnId = turnId,
                RequestSnapshotJson = invalidSnapshot ? "invalid snapshot" : null,
                RecoverAfter = TimeProvider.System.GetUtcNow(),
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var record = await db.Events.SingleAsync(row => row.Id == eventId, TestContext.Current.CancellationToken);
        var transportDelivery = new TransportDelivery
        {
            Topic = EventTopics.Delivery,
            Message = EventDeliveryMessageFactory.Create(record, deliveryId),
            MessageId = Guid.CreateVersion7().ToString("D"),
            DeliveryCount = 1,
        };
        db.ChangeTracker.Clear();

        await services
            .GetRequiredService<EventDeliveryProcessor>()
            .ProcessAsync(transportDelivery, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var delivery = await db
            .EventDeliveries.AsNoTracking()
            .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryState.RetryScheduled, delivery.State);
        Assert.Equal(1, delivery.Attempt);
        Assert.Equal(turnId, delivery.TurnId);
        Assert.Equal(invalidSnapshot ? "invalid snapshot" : null, delivery.RequestSnapshotJson);
    }

    [Fact]
    public async Task ProcessAsync_AgentReference_OmitsBinaryEventData()
    {
        using var user = PushUser(OwnerUserId);
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var eventId = await StageEventAsync(services, "example.updated", "ext:019a4c17-0000-7000-8000-000000000001");
        var deliveryId = Guid.CreateVersion7();
        var subscriptionId = Guid.CreateVersion7();
        db.EventSubscriptions.Add(
            new EventSubscription
            {
                Id = subscriptionId,
                CreateBy = OwnerUserId,
                Name = "binary-agent-subscription",
                Enabled = true,
                TargetKind = EventSubscriptionTargetKind.Agent,
                AgentId = Guid.CreateVersion7(),
                ProjectId = Guid.CreateVersion7(),
                Instructions = "binary instructions",
                PermissionMode = 0,
            }
        );
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                CreateBy = OwnerUserId,
                EventId = eventId,
                SubscriptionId = subscriptionId,
                State = EventDeliveryState.Pending,
                RecoverAfter = TimeProvider.System.GetUtcNow(),
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var record = await db.Events.SingleAsync(row => row.Id == eventId, TestContext.Current.CancellationToken);
        var binary = new byte[] { 0, 1, 127, 255 };
        var transportDelivery = new TransportDelivery
        {
            Topic = EventTopics.Delivery,
            Message = EventDeliveryMessageFactory.Create(record, deliveryId) with
            {
                Type = "binary.transport",
                DataContentType = "application/octet-stream",
                Data = new CloudEventData { Kind = CloudEventDataKind.Binary, Binary = binary },
            },
            MessageId = Guid.CreateVersion7().ToString("D"),
            DeliveryCount = 1,
        };
        db.ChangeTracker.Clear();

        await services
            .GetRequiredService<EventDeliveryProcessor>()
            .ProcessAsync(transportDelivery, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var delivery = await db
            .EventDeliveries.AsNoTracking()
            .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
        var snapshot = Assert.IsType<string>(delivery.RequestSnapshotJson);
        var request = Assert.IsType<BackgroundExecutionRequest>(
            JsonUtil.Deserialize<BackgroundExecutionRequest>(snapshot)
        );
        var reference = Assert.IsType<AgwTextContent>(request.Input.Contents[1]).Content;
        Assert.Contains("\"specversion\":\"1.0\"", reference);
        Assert.Contains("\"type\":\"binary.transport\"", reference);
        Assert.Contains("Binary event data omitted. Content type: application/octet-stream; size: 4 bytes.", reference);
        Assert.DoesNotContain("data_base64", reference);
        Assert.DoesNotContain(Convert.ToBase64String(binary), reference);
    }

    [Fact]
    public async Task HandleAsync_LateCompletionForRequeuedDeliveryWithSameTurnId_SettlesDelivery()
    {
        using var user = PushUser(OwnerUserId);
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var deliveryId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var startedAt = TimeProvider.System.GetUtcNow().AddMinutes(-1);
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                CreateBy = OwnerUserId,
                EventId = Guid.CreateVersion7(),
                SubscriptionId = Guid.CreateVersion7(),
                State = EventDeliveryState.Pending,
                Attempt = 1,
                TurnId = turnId,
                RecoverAfter = TimeProvider.System.GetUtcNow(),
            }
        );
        db.EventDeliveryAttempts.Add(
            new EventDeliveryAttempt
            {
                Id = Guid.CreateVersion7(),
                DeliveryId = deliveryId,
                Attempt = 1,
                HostId = "events-test-host",
                StartedAt = startedAt,
                EndedAt = startedAt,
                Result = EventDeliveryAttemptResult.Retry,
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var completionEventId = await StageEventAsync(
            services,
            "agw.execution.completed",
            "agw.execution",
            JsonSerializer.SerializeToElement(
                new
                {
                    turnId,
                    caller = $"delivery:{deliveryId:D}",
                    correlationKey = $"delivery:{deliveryId:D}",
                    state = "Completed",
                    error = (string?)null,
                }
            ),
            "/agw/executions"
        );
        var completionEvent = await db.Events.SingleAsync(
            row => row.Id == completionEventId,
            TestContext.Current.CancellationToken
        );
        var source = await db.EventSources.SingleAsync(
            row => row.Id == completionEvent.SourceId,
            TestContext.Current.CancellationToken
        );

        await services
            .GetRequiredService<IEventExecutionCompletionHandler>()
            .HandleAsync(completionEvent, source, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var delivery = await db
            .EventDeliveries.AsNoTracking()
            .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
        var attempt = await db
            .EventDeliveryAttempts.AsNoTracking()
            .SingleAsync(row => row.DeliveryId == deliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryState.Succeeded, delivery.State);
        Assert.Equal(EventDeliveryAttemptResult.Succeeded, attempt.Result);
    }

    [Fact]
    public async Task HandleAsync_LateCompletionForOlderTurnId_DoesNotChangeNewExecution()
    {
        using var user = PushUser(OwnerUserId);
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var deliveryId = Guid.CreateVersion7();
        var oldTurnId = Guid.CreateVersion7();
        var newTurnId = Guid.CreateVersion7();
        var startedAt = TimeProvider.System.GetUtcNow().AddMinutes(-1);
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                CreateBy = OwnerUserId,
                EventId = Guid.CreateVersion7(),
                SubscriptionId = Guid.CreateVersion7(),
                State = EventDeliveryState.Running,
                Attempt = 2,
                TurnId = newTurnId,
                RecoverAfter = TimeProvider.System.GetUtcNow(),
            }
        );
        db.EventDeliveryAttempts.Add(
            new EventDeliveryAttempt
            {
                Id = Guid.CreateVersion7(),
                DeliveryId = deliveryId,
                Attempt = 2,
                HostId = "events-test-host",
                StartedAt = startedAt,
                EndedAt = startedAt,
                Result = EventDeliveryAttemptResult.Retry,
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var completionEventId = await StageEventAsync(
            services,
            "agw.execution.completed",
            "agw.execution",
            JsonSerializer.SerializeToElement(
                new
                {
                    turnId = oldTurnId,
                    caller = $"delivery:{deliveryId:D}",
                    correlationKey = $"delivery:{deliveryId:D}",
                    state = "Completed",
                    error = (string?)null,
                }
            ),
            "/agw/executions"
        );
        var completionEvent = await db.Events.SingleAsync(
            row => row.Id == completionEventId,
            TestContext.Current.CancellationToken
        );
        var source = await db.EventSources.SingleAsync(
            row => row.Id == completionEvent.SourceId,
            TestContext.Current.CancellationToken
        );
        var version = (
            await db
                .EventDeliveries.AsNoTracking()
                .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken)
        ).Version;

        await services
            .GetRequiredService<IEventExecutionCompletionHandler>()
            .HandleAsync(completionEvent, source, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var delivery = await db
            .EventDeliveries.AsNoTracking()
            .SingleAsync(row => row.Id == deliveryId, TestContext.Current.CancellationToken);
        Assert.Equal(EventDeliveryState.Running, delivery.State);
        Assert.Equal(newTurnId, delivery.TurnId);
        Assert.Equal(version, delivery.Version);
    }

    private static async Task<Guid> StageEventAsync(
        IServiceProvider services,
        string type,
        string publisherKey,
        JsonElement? data = null,
        string? source = null
    )
    {
        var request = new EventStagingRequest
        {
            PublisherKey = publisherKey,
            Event = new CloudEventDraft
            {
                Id = Guid.CreateVersion7().ToString("D"),
                Source = source,
                Type = type,
                DataContentType = data.HasValue ? "application/json" : null,
                Data = data is { } value
                    ? new CloudEventData
                    {
                        Kind = CloudEventDataKind.Json,
                        Text = value.GetRawText(),
                        Json = value,
                    }
                    : null,
            },
        };
        var result = await services
            .GetRequiredService<IEventStaging>()
            .StageAsync(request, TestContext.Current.CancellationToken);
        await services.GetRequiredService<AgwDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);
        return result.EventId;
    }

    private static CloudEventData JsonData(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new CloudEventData
        {
            Kind = CloudEventDataKind.Json,
            Text = json,
            Json = document.RootElement.Clone(),
        };
    }

    private static IDisposable PushUser(string userId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
        );

    private sealed class TimeControlledHost : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _services;

        private TimeControlledHost(string root, ServiceProvider services)
        {
            _root = root;
            _services = services;
        }

        public IServiceProvider Services => _services;

        public static async Task<TimeControlledHost> CreateAsync(
            TestTimeProvider timeProvider,
            IReadOnlyDictionary<string, string?>? settings = null
        )
        {
            var root = Path.Combine(AppContext.BaseDirectory, $"event-delivery-composition-{Guid.CreateVersion7():N}");
            var descriptors = new ServiceCollection();
            ModuleCompositionTestHost.ConfigureServices(descriptors, root, settings);
            descriptors.RemoveAll<TimeProvider>();
            descriptors.AddSingleton<TimeProvider>(timeProvider);
            var services = descriptors.BuildServiceProvider();
            await ModuleCompositionTestHost.InitializeAsync(services);
            return new TimeControlledHost(root, services);
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
