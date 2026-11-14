using Agw.Agents;
using Agw.Agents.Execution;
using Agw.Auth.Contracts;
using Agw.Auth.Extensions;
using Agw.Events;
using Agw.Events.Contracts;
using Agw.Events.Contracts.Transport;
using Agw.Extensions;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Packages;
using Agw.Files;
using Agw.Host.Data;
using Agw.Host.Runtime;
using Agw.Infrastructure;
using Agw.Infrastructure.Data;
using Agw.Integrations.Extensions;
using Agw.Jobs;
using Agw.Notifications;
using Agw.Projects;
using Agw.Providers;
using Agw.Settings;
using Agw.Settings.Contracts;
using Agw.Setup.Services;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Runtime;
using Agw.Skills;
using Agw.Tools;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Agw.Tests;

internal sealed class ModuleCompositionTestHost : IAsyncDisposable
{
    private readonly string _root;
    private readonly ServiceProvider _services;
    private readonly bool _ownsRoot;

    private ModuleCompositionTestHost(string root, ServiceProvider services, bool ownsRoot)
    {
        _root = root;
        _services = services;
        _ownsRoot = ownsRoot;
    }

    public IServiceProvider Services => _services;

    public static async Task<ModuleCompositionTestHost> CreateAsync(
        bool outboxEnabled = false,
        string? rootDirectory = null,
        string transportProvider = "InProcess",
        string? redisConnectionString = null,
        TimeSpan? staleAfter = null,
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null,
        bool addExecutionTransport = false
    )
    {
        var root =
            rootDirectory ?? Path.Combine(AppContext.BaseDirectory, $"module-composition-{Guid.CreateVersion7():N}");
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Events:Outbox:Enabled"] = outboxEnabled.ToString(),
            ["Events:Transport:Provider"] = transportProvider,
            ["Events:Transport:Redis:ConnectionString"] = redisConnectionString,
        };
        if (staleAfter is { } age)
        {
            overrides["Events:Outbox:StaleAfter"] = age.ToString("c");
        }
        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                overrides[key] = value;
            }
        }

        var services = new ServiceCollection();
        ConfigureServices(services, root, overrides, addExecutionTransport);
        configureServices?.Invoke(services);
        var provider = services.BuildServiceProvider();
        await InitializeAsync(provider);
        return new ModuleCompositionTestHost(root, provider, rootDirectory is null);
    }

    public static IConfiguration ConfigureServices(
        IServiceCollection services,
        string root,
        IReadOnlyDictionary<string, string?>? settings = null,
        bool addExecutionTransport = false
    )
    {
        var databasePath = Path.Combine(root, "events.db");
        var extensionDirectory = Path.Combine(root, "extension-sources");
        var keysDirectory = Path.Combine(root, "data-protection-keys");
        Directory.CreateDirectory(extensionDirectory);
        Directory.CreateDirectory(keysDirectory);
        var paths = AgwDataPaths.Resolve(Path.Combine(root, "data"), root, Path.Combine(root, "logs"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["Database:ConnectionString"] = $"Data Source={databasePath};Pooling=False",
                    ["Execution:Provider"] = "InProcess",
                    ["Events:Outbox:Enabled"] = "false",
                    ["Events:Delivery:MaxAttempts"] = "3",
                    ["Extensions:Directory"] = extensionDirectory,
                }
            )
            .AddInMemoryCollection(settings)
            .Build();

        services.AddLogging();
        services.AddSingleton(paths);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IHostEnvironment>(_ =>
            Microsoft
                .Extensions.Hosting.Host.CreateApplicationBuilder(
                    new HostApplicationBuilderSettings
                    {
                        ContentRootPath = AppContext.BaseDirectory,
                        DisableDefaults = true,
                    }
                )
                .Environment
        );
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
        services.AddHttpClient();
        services.AddScoped<IEntityAuditUserIdProvider, EntityAuditUserIdProvider>();
        services.AddScoped<IExtensionDeploymentIdentity, ExtensionDeploymentIdentity>();
        services
            .AddTools(configuration)
            .AddAgents(configuration)
            .AddAgentExecution(
                configuration,
                new Agw.Agents.Execution.DependencyInjection.RegistrationOptions(
                    AddExecutionTransport: addExecutionTransport,
                    AddDistributedWorker: false,
                    AddTraceCollector: false
                )
            )
            .AddFiles(configuration)
            .AddInfrastructure(configuration)
            .AddExtensionInfrastructure()
            .AddExtensions(configuration, "Standalone")
            .AddExtensionCapabilities()
            .AddEvents(runWorkers: false)
            .AddNotifications(configuration, runWorkers: false)
            .AddJobs()
            .AddProviders(configuration)
            .AddSkills(configuration)
            .AddProjects(configuration)
            .AddAuth()
            .AddSettings()
            .AddSetup(configuration)
            .AddIntegrations(configuration);
        services.AddExtensionHostCapability<Agw.Projects.Contracts.Runtime.IProjectDefaultResolver>();
        services.AddExtensionHostCapability<IExtensionCallerAccessor>();
        services.AddExtensionHostCapability<IEventPublisher>();
        services.AddExtensionHostCapability<IEventContextFacade>();
        services.AddExtensionHostCapability<Agw.Agents.Contracts.Execution.IBackgroundExecutionFacade>();
        return configuration;
    }

    public static async Task InitializeAsync(IServiceProvider services)
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        await services.GetRequiredService<ExtensionPackageCatalog>().InitializeAsync(cancellationToken);
        await using var scope = services.CreateAsyncScope();
        var scoped = scope.ServiceProvider;
        await scoped.GetRequiredService<AgwDbContext>().Database.EnsureCreatedAsync(cancellationToken);

        var transportProvider = services.GetRequiredService<IConfiguration>()["Events:Transport:Provider"];
        if (string.Equals(transportProvider, "Redis", StringComparison.OrdinalIgnoreCase))
        {
            using var systemScope = UserInfoUtil.PushSystemScope();
            await scoped
                .GetRequiredService<ISettingsStore>()
                .TryCreateGlobalAsync(
                    ExtensionDeploymentSettings.Key,
                    new ExtensionDeploymentSettings { ServerId = Guid.CreateVersion7().ToString("D") },
                    cancellationToken
                );
        }
    }

    public static async Task<TransportDelivery> ReadTransportDeliveryAsync(IEventTransport transport, string topic)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), TimeProvider.System);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            Xunit.TestContext.Current.CancellationToken
        );
        await using var enumerator = transport
            .ConsumeAsync(topic, "event-delivery-integration", cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Xunit.Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        if (_ownsRoot && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
