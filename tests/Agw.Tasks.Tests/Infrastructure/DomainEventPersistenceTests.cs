using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.EventBus.Abstractions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tasks.Tests.Infrastructure;

public class DomainEventPersistenceTests
{
    [Fact]
    public async Task SaveChangesAsync_WhenEntityHasDomainEvent_PublishesAfterPersistenceAndClearsEvents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var services = new ServiceCollection();
        var probe = new PublishedJobProbe();

        services.AddSingleton(connection);
        services.AddSingleton(probe);
        services.AddSingleton<IDomainEventBus, InMemoryDomainEventBus>();
        services.AddScoped<IDomainEventHandler<TestJobPersistedDomainEvent>, TestJobPersistedDomainEventHandler>();
        services.AddDbContext<AgwDbContext>((serviceProvider, options) =>
        {
            options.UseSqlite(serviceProvider.GetRequiredService<SqliteConnection>())
                .UseSnakeCaseNamingConvention();
        });

        await using var serviceProvider = services.BuildServiceProvider();

        await using (var setupScope = serviceProvider.CreateAsyncScope())
        {
            var setupContext = setupScope.ServiceProvider.GetRequiredService<AgwDbContext>();
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var jobId = Guid.NewGuid();

        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            var job = CreateJob(jobId);

            job.AddDomainEvent(new TestJobPersistedDomainEvent(jobId));
            dbContext.Jobs.Add(job);

            await dbContext.SaveChangesAsync(cancellationToken);

            Assert.Empty(job.DomainEvents);
        }

        Assert.Equal(jobId, probe.JobId);
        Assert.True(probe.WasPersistedBeforeHandlerRan);
    }

    private static Job CreateJob(Guid jobId)
    {
        return new Job
        {
            Id = jobId,
            ProjectId = Guid.NewGuid(),
            Name = "Domain event test",
            TriggerType = TriggerType.Once,
            TriggerValue = DateTimeOffset.UtcNow.ToString("O"),
            NextRunTime = DateTimeOffset.UtcNow,
            Status = JobStatus.Pending,
            IsEnabled = true,
            CreateBy = "tester",
            CreateTime = DateTime.UtcNow,
            UpdateBy = "tester",
            UpdateTime = DateTime.UtcNow
        };
    }

    private sealed record TestJobPersistedDomainEvent(Guid JobId) : IDomainEvent;

    private sealed class PublishedJobProbe
    {
        public Guid JobId { get; set; }

        public bool WasPersistedBeforeHandlerRan { get; set; }
    }

    private sealed class TestJobPersistedDomainEventHandler(
        AgwDbContext dbContext,
        PublishedJobProbe probe) : IDomainEventHandler<TestJobPersistedDomainEvent>
    {
        public async Task HandleAsync(TestJobPersistedDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            probe.JobId = domainEvent.JobId;
            probe.WasPersistedBeforeHandlerRan = await dbContext.Jobs
                .AsNoTracking()
                .AnyAsync(job => job.Id == domainEvent.JobId, cancellationToken);
        }
    }
}
