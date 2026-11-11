using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Events.Configuration;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Infrastructure.Events;
using Agw.Infrastructure.Notifications;
using Agw.Notifications.Application;
using Agw.Notifications.Configuration;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Abstractions;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Agw.Notifications.Tests;

internal sealed class NotificationTestFixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AgwDbContext> _dbOptions;
    private readonly List<NotificationTestScope> _scopes = [];

    private NotificationTestFixture(
        SqliteConnection connection,
        DbContextOptions<AgwDbContext> dbOptions,
        TestTimeProvider timeProvider
    )
    {
        _connection = connection;
        _dbOptions = dbOptions;
        TimeProvider = timeProvider;
    }

    public TestTimeProvider TimeProvider { get; }

    public static async Task<NotificationTestFixture> CreateAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(cancellationToken);
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 1, 0, 0, TimeSpan.Zero));
        var auditUserIdProvider = new TestAuditUserIdProvider();
        var dbOptions = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                new EntityCreatorInterceptor(auditUserIdProvider, timeProvider),
                new EntityModifierInterceptor(auditUserIdProvider, timeProvider)
            )
            .Options;
        var fixture = new NotificationTestFixture(connection, dbOptions, timeProvider);
        var scope = fixture.CreateScope();
        await scope.DbContext.Database.EnsureCreatedAsync(cancellationToken);
        return fixture;
    }

    public NotificationTestScope CreateScope()
    {
        var dbContext = new AgwDbContext(_dbOptions);
        var pending = new PendingTransportMessages();
        var eventOptions = Options.Create(new EventsOptions());
        var outbox = new EventOutboxStaging(dbContext, eventOptions, pending, TimeProvider);
        var eventStaging = new EventStaging(
            dbContext,
            outbox,
            pending,
            InMemoryApplicationLock.Shared,
            new CloudEventEnvelopeSerializer(),
            eventOptions,
            TimeProvider
        );
        var transaction = new NotificationTransaction(dbContext, eventStaging, outbox);
        var scope = new NotificationTestScope(dbContext, pending, eventStaging, transaction, TimeProvider);
        _scopes.Add(scope);
        return scope;
    }

    public IDisposable PushUser(string userId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "notification-test"))
        );

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.Pending.DisposeAsync();
            await scope.DbContext.DisposeAsync();
        }
        await _connection.DisposeAsync();
    }

    internal sealed record NotificationTestScope(
        AgwDbContext DbContext,
        PendingTransportMessages Pending,
        EventStaging EventStaging,
        NotificationTransaction Transaction,
        TestTimeProvider TimeProvider
    )
    {
        public NotificationAppService CreateAppService(NotificationDispatchOptions? options = null) =>
            new(
                DbContext,
                Transaction,
                EventStaging,
                TimeProvider,
                Options.Create(options ?? new NotificationDispatchOptions()),
                callerAccessor: null,
                channelCatalog: null
            );
    }

    internal sealed class TestAuditUserIdProvider : IEntityAuditUserIdProvider
    {
        public string GetUserId() => UserInfoUtil.UserId ?? "notification-test";
    }
}
