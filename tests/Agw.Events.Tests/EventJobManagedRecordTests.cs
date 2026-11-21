using System.Security.Claims;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Application;
using Agw.Events.Application.Sources;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Agw.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Events.Tests;

public sealed class EventJobManagedRecordTests
{
    private const string Owner = "owner";

    [Theory]
    [InlineData("update")]
    [InlineData("enable")]
    [InlineData("delete")]
    public async Task SourceMutation_JobManagedSource_ThrowsEventSourceManaged(string operation)
    {
        using var user = PushOwner();
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.CreateVersion7();
        using var config = JsonDocument.Parse(
            """{"kind":"Interval","interval":"PT1M","timeZone":"UTC","event":{"type":"job.timer.fired"}}"""
        );
        var source = new EventSource
        {
            Id = id,
            CreateBy = Owner,
            Name = "Job Timer",
            Kind = EventSourceKind.Timer,
            CeSource = $"/agw/sources/{id:N}",
            ConfigJson = config.RootElement.GetRawText(),
            ManagedBy = EventManagedBy.Job,
            ManagedKey = Guid.CreateVersion7().ToString("D"),
            Enabled = true,
        };
        db.EventSources.Add(source);
        await db.SaveChangesAsync(cancellationToken);
        var version = source.Version;

        var service = services.GetRequiredService<EventSourceAppService>();
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            operation switch
            {
                "update" => service.UpdateAsync(
                    new EventSourceUpdateCommand
                    {
                        Id = id,
                        Version = version,
                        Name = "Updated Timer",
                        Config = config.RootElement,
                    },
                    cancellationToken
                ),
                "enable" => service.SetEnabledAsync(
                    new EventSourceEnableCommand
                    {
                        Id = id,
                        Version = version,
                        Enabled = false,
                    },
                    cancellationToken
                ),
                "delete" => service.DeleteAsync(
                    new EventSourceVersionCommand { Id = id, Version = version },
                    cancellationToken
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            }
        );

        Assert.Equal(ErrorCodes.EventSourceManaged.Code, exception.Code);
        Assert.Equal("Event source is managed by a Job.", exception.Message);
        var persisted = await db.EventSources.AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken);
        Assert.Equal(version, persisted.Version);
        Assert.True(persisted.Enabled);
        Assert.Null(persisted.DeletedAt);
    }

    [Theory]
    [InlineData("update", EventManagedBy.Job, "Event subscription is managed by a Job.")]
    [InlineData("delete", EventManagedBy.Job, "Event subscription is managed by a Job.")]
    [InlineData("update", "ext:019a4c17-0000-7000-8000-000000000001", "Event subscription is managed by an extension.")]
    [InlineData("delete", "ext:019a4c17-0000-7000-8000-000000000001", "Event subscription is managed by an extension.")]
    public async Task SubscriptionMutation_ManagedSubscription_ThrowsEventSubscriptionManaged(
        string operation,
        string managedBy,
        string message
    )
    {
        using var user = PushOwner();
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AgwDbContext>();
        var cancellationToken = TestContext.Current.CancellationToken;
        var subscription = await SeedSubscriptionAsync(db, managedBy, cancellationToken);
        var service = services.GetRequiredService<EventManagementAppService>();

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            operation switch
            {
                "update" => service.SaveSubscriptionAsync(
                    new EventSubscriptionRequest
                    {
                        Id = subscription.Id,
                        Version = subscription.Version,
                        Name = "Updated subscription",
                        EventTypes = ["job.timer.fired"],
                        TargetKind = EventSubscriptionTargetKind.Agent,
                    },
                    create: false,
                    cancellationToken
                ),
                "delete" => service.DeleteSubscriptionAsync(
                    new EventSubscriptionDeleteCommand(subscription.Id, subscription.Version),
                    cancellationToken
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            }
        );

        Assert.Equal(ErrorCodes.EventSubscriptionManaged.Code, exception.Code);
        Assert.Equal(message, exception.Message);
        Assert.True(await db.EventSubscriptions.AnyAsync(row => row.Id == subscription.Id, cancellationToken));
    }

    [Fact]
    public async Task GetSubscriptionAsync_JobManagedSubscription_ReturnsManagedByAndManagedKey()
    {
        using var user = PushOwner();
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var cancellationToken = TestContext.Current.CancellationToken;
        var subscription = await SeedSubscriptionAsync(
            services.GetRequiredService<AgwDbContext>(),
            EventManagedBy.Job,
            cancellationToken
        );

        var response = await services
            .GetRequiredService<EventManagementAppService>()
            .GetSubscriptionAsync(subscription.Id, cancellationToken);

        Assert.Equal("job", response.ManagedBy);
        Assert.Equal(subscription.ManagedKey, response.ManagedKey);
    }

    private static async Task<EventSubscription> SeedSubscriptionAsync(
        AgwDbContext db,
        string managedBy,
        CancellationToken cancellationToken
    )
    {
        var subscription = new EventSubscription
        {
            Id = Guid.CreateVersion7(),
            CreateBy = Owner,
            Name = "Managed subscription",
            Enabled = true,
            TargetKind = EventSubscriptionTargetKind.Agent,
            ManagedBy = managedBy,
            ManagedKey = Guid.CreateVersion7().ToString("D"),
            EventTypesJson = """["job.timer.fired"]""",
        };
        db.EventSubscriptions.Add(subscription);
        await db.SaveChangesAsync(cancellationToken);
        return subscription;
    }

    private static IDisposable PushOwner() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Owner)], "test"))
        );
}
