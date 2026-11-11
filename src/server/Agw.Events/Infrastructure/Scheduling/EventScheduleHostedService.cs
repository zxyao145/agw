using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Events.Application;
using Agw.Events.Application.Persistence;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Events.Infrastructure.Scheduling;

public sealed class EventScheduleHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IServerInitializationState _initialization;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventScheduleHostedService> _logger;

    public EventScheduleHostedService(
        IServiceScopeFactory scopes,
        IServerInitializationState initialization,
        TimeProvider timeProvider,
        ILogger<EventScheduleHostedService> logger
    )
    {
        _scopes = scopes;
        _initialization = initialization;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_initialization.IsInitialized)
            {
                try
                {
                    await ScanAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "Event schedule scan failed");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, stoppingToken);
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        await using var discovery = _scopes.CreateAsyncScope();
        var db = discovery.ServiceProvider.GetRequiredService<IEventsDbContext>();
        var now = _timeProvider.GetUtcNow();
        List<EventSchedule> schedules;
        using (UserInfoUtil.PushSystemScope())
            schedules = await db
                .EventSchedules.AsNoTracking()
                .Where(row => row.State == EventScheduleState.Active && row.DueAt <= now)
                .OrderBy(row => row.DueAt)
                .Take(200)
                .ToListAsync(cancellationToken);
        foreach (var schedule in schedules)
        {
            try
            {
                using var user = UserInfoUtil.Push(
                    new ClaimsPrincipal(
                        new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, schedule.CreateBy!)], "event-schedule")
                    )
                );
                await using var scope = _scopes.CreateAsyncScope();
                await scope
                    .ServiceProvider.GetRequiredService<EventScheduleRunner>()
                    .RunAsync(schedule.Id, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Event schedule {ScheduleId} failed", schedule.Id);
            }
        }
    }
}
