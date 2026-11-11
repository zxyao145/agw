using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Observability;
using Agw.Shared.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Infrastructure.Events.Sources;

public sealed class EventSourceScanService : IEventSourceScanService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventSourceScanService> _logger;

    public EventSourceScanService(
        IServiceScopeFactory scopes,
        TimeProvider timeProvider,
        ILogger<EventSourceScanService> logger
    )
    {
        _scopes = scopes;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ScanDueAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<EventSourceTarget> targets;
        await using (var discovery = _scopes.CreateAsyncScope())
        {
            var db = discovery.ServiceProvider.GetRequiredService<IEventsDbContext>();
            using (UserInfoUtil.PushSystemScope())
            {
                var now = _timeProvider.GetUtcNow();
                targets = await db
                    .EventSources.AsNoTracking()
                    .Where(source =>
                        source.Enabled
                        && source.DeletedAt == null
                        && source.CreateBy != null
                        && source.NextRunAt <= now
                        && (source.Kind == EventSourceKind.HttpPoll || source.Kind == EventSourceKind.Provider)
                    )
                    .OrderBy(source => source.NextRunAt)
                    .Take(100)
                    .Select(source => new EventSourceTarget(source.Id, source.CreateBy!))
                    .ToArrayAsync(cancellationToken);
            }
        }

        foreach (var target in targets)
        {
            long started = _timeProvider.GetTimestamp();
            using var userScope = UserInfoUtil.Push(CreateUser(target.UserId));
            Exception? failure = null;
            await using (var sourceScope = _scopes.CreateAsyncScope())
            {
                try
                {
                    await sourceScope
                        .ServiceProvider.GetRequiredService<EventSourceScanWorker>()
                        .ScanAsync(target.Id, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failure = exception;
                }
            }

            EventTelemetry.PollDuration.Record(_timeProvider.GetElapsedTime(started).TotalSeconds);

            if (failure is null)
            {
                continue;
            }

            _logger.LogWarning(failure, "Event source scan failed for {SourceId}", target.Id);
            EventTelemetry.PollFailures.Add(1);
            await using var failureScope = _scopes.CreateAsyncScope();
            await failureScope
                .ServiceProvider.GetRequiredService<EventSourceScanWorker>()
                .RecordFailureAsync(target.Id, failure.Message, cancellationToken);
        }
    }

    private static ClaimsPrincipal CreateUser(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "event-source-scan"));

    private sealed record EventSourceTarget(Guid Id, string UserId);
}
