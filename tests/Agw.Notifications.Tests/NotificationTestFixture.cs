using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Events.Application;
using Agw.Events.Application.Sources;
using Agw.Events.Configuration;
using Agw.Events.Domain.Services;
using Agw.Events.Infrastructure;
using Agw.Extensions;
using Agw.Extensions.Contracts;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Infrastructure.Events;
using Agw.Infrastructure.Notifications;
using Agw.Notifications.Application;
using Agw.Notifications.Configuration;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Runtime;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Notifications.Tests;

internal sealed class NotificationTestFixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AgwDbContext> _dbOptions;
    private readonly List<NotificationTestScope> _scopes = [];
    private readonly ServiceProvider _extensionServices;
    private readonly string _extensionRoot;

    private NotificationTestFixture(
        SqliteConnection connection,
        DbContextOptions<AgwDbContext> dbOptions,
        TestTimeProvider timeProvider,
        ServiceProvider extensionServices,
        string extensionRoot
    )
    {
        _connection = connection;
        _dbOptions = dbOptions;
        TimeProvider = timeProvider;
        _extensionServices = extensionServices;
        _extensionRoot = extensionRoot;
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
        var extensionRoot = Path.Combine(
            AppContext.BaseDirectory,
            $"notification-extensions-{Guid.CreateVersion7():N}"
        );
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(timeProvider);
        RegisterExtensionCatalog(services, extensionRoot);
        var extensionServices = services.BuildServiceProvider();
        var fixture = new NotificationTestFixture(
            connection,
            dbOptions,
            timeProvider,
            extensionServices,
            extensionRoot
        );
        var scope = fixture.CreateScope();
        await scope.DbContext.Database.EnsureCreatedAsync(cancellationToken);
        return fixture;
    }

    internal static void RegisterExtensionCatalog(IServiceCollection services, string extensionRoot)
    {
        Directory.CreateDirectory(extensionRoot);
        services.AddSingleton(
            AgwDataPaths.Resolve(
                Path.Combine(extensionRoot, "data"),
                extensionRoot,
                Path.Combine(extensionRoot, "logs")
            )
        );
        services.AddSingleton<IHostEnvironment>(_ =>
            Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings { ContentRootPath = extensionRoot, DisableDefaults = true }
            ).Environment
        );
        services.AddExtensions(new ConfigurationBuilder().Build(), "Standalone");
    }

    public NotificationTestScope CreateScope()
    {
        var dbContext = new AgwDbContext(_dbOptions);
        var pending = new PendingTransportMessages();
        var eventOptions = Options.Create(new EventsOptions());
        var outbox = new EventOutboxStaging(dbContext, eventOptions, pending, TimeProvider);
        var extensions = _extensionServices.GetRequiredService<IExtensionGenerationCatalog>();
        var publisherSources = new EventPublisherSources(
            dbContext,
            new EventIngestTransaction(dbContext, pending, InMemoryApplicationLock.Shared),
            extensions,
            new EventSourceNameDomainService(new EventSourceNameRepository(dbContext))
        );
        var eventStaging = new EventStaging(
            publisherSources,
            new EventRecordStaging(dbContext, outbox, new CloudEventEnvelopeSerializer(), eventOptions, TimeProvider),
            extensions,
            TimeProvider,
            _extensionServices.GetRequiredService<ILogger<EventStaging>>()
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
        await _extensionServices.DisposeAsync();
        await _connection.DisposeAsync();
        if (Directory.Exists(_extensionRoot))
            Directory.Delete(_extensionRoot, recursive: true);
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
                new EventContextFacade(DbContext),
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
