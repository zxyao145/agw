using System.Data.Common;
using System.Text.Json;
using Agw.Agents.Contracts.Execution;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Extensions.Application.Packages;
using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Packages;
using Agw.Extensions.Infrastructure.Runtime;
using Agw.Host.Runtime;
using Agw.Infrastructure;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Settings;
using Agw.Projects;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.Runtime;
using Agw.Settings;
using Agw.Settings.Application.Persistence;
using Agw.Settings.Contracts;
using Agw.Shared.Configuration;
using Agw.Shared.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Agw.Extensions.TestSupport;

public sealed class ExtensionRuntimeIntegrationHost : IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly ServiceProvider _services;
    private readonly AgwDataPaths _paths;

    private ExtensionRuntimeIntegrationHost(
        DbConnection connection,
        ServiceProvider services,
        AgwDataPaths paths,
        string packageDirectory
    )
    {
        _connection = connection;
        _services = services;
        _paths = paths;
        PackageDirectory = packageDirectory;
    }

    public ExtensionRuntime Runtime => _services.GetRequiredService<ExtensionRuntime>();

    public IServiceProvider Services => _services;

    public DbConnection DatabaseConnection => _connection;

    public string PackageDirectory { get; }

    public string DataDirectory(Guid extensionId) =>
        Path.Combine(_paths.Root, "extensions", "data", extensionId.ToString("N"));

    public static async Task<ExtensionRuntimeIntegrationHost> CreateAsync(
        string packageDirectory,
        Action<IServiceCollection>? configureHostServices = null,
        string hostRole = "Standalone",
        DatabaseProvider databaseProvider = DatabaseProvider.Sqlite,
        string? databaseConnectionString = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!Directory.Exists(packageDirectory))
            throw new DirectoryNotFoundException(packageDirectory);

        string manifestJson = await File.ReadAllTextAsync(
            Path.Combine(packageDirectory, "manifest.json"),
            cancellationToken
        );
        using JsonDocument manifest = JsonDocument.Parse(manifestJson);
        string packageName = manifest.RootElement.GetProperty("name").GetString()!;
        string extensionRoot = Path.Combine(AppContext.BaseDirectory, $"extension-runtime-{Guid.CreateVersion7():N}");
        string packageRoot = Path.Combine(extensionRoot, "source");
        string targetPackageDirectory = Path.Combine(packageRoot, packageName);
        foreach (string sourcePath in Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(packageDirectory, sourcePath);
            string targetPath = Path.Combine(targetPackageDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using FileStream input = File.OpenRead(sourcePath);
            await using FileStream output = File.Create(targetPath);
            await input.CopyToAsync(output, cancellationToken);
        }

        AgwDataPaths paths = AgwDataPaths.Resolve(extensionRoot, AppContext.BaseDirectory);
        Directory.CreateDirectory(paths.Root);
        string connectionString =
            databaseConnectionString ?? $"Data Source={Path.Combine(paths.Root, "extension-runtime.db")}";
        DbConnection connection = databaseProvider switch
        {
            DatabaseProvider.Sqlite => new SqliteConnection(connectionString),
            DatabaseProvider.Postgres => new NpgsqlConnection(connectionString),
            _ => throw new ArgumentOutOfRangeException(nameof(databaseProvider)),
        };
        await connection.OpenAsync(cancellationToken);

        IConfiguration extensionConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Extensions:Directory"] = packageRoot,
                    ["Extensions:PublicBaseUrl"] = "https://agw.integration.test",
                }
            )
            .Build();
        ServiceCollection serviceCollection = new();
        serviceCollection
            .AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.Root, "extensions", "keys")));
        serviceCollection.AddSingleton<IApplicationLock, InMemoryApplicationLock>();
        serviceCollection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
        serviceCollection
            .AddSingleton(paths)
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment(AppContext.BaseDirectory))
            .AddSingleton<IConfiguration>(extensionConfiguration)
            .Configure<DatabaseSettings>(settings =>
            {
                settings.Provider = databaseProvider;
                settings.ConnectionString = connectionString;
            })
            .AddDbContext<AgwDbContext>(builder =>
            {
                if (databaseProvider == DatabaseProvider.Sqlite)
                    builder.UseSqlite(connectionString);
                else
                    builder.UseNpgsql(connectionString);
                builder.UseSnakeCaseNamingConvention();
            })
            .AddScoped<IProjectsDbContext>(provider => provider.GetRequiredService<AgwDbContext>())
            .AddScoped<ISettingsDbContext>(provider => provider.GetRequiredService<AgwDbContext>())
            .AddScoped<ISettingsPersistence, EfSettingsPersistence>()
            .AddScoped<IExtensionExecutionUsage, DurableExecutionScopeMaintenance>()
            .AddScoped<ICurrentUser, UserInfoService>()
            .AddScoped<IUserInfoService, UserInfoService>()
            .AddSettings()
            .AddSingleton<IExtensionDeploymentIdentity, ExtensionDeploymentIdentity>()
            .AddExtensionInfrastructure()
            .AddExtensions(extensionConfiguration, hostRole)
            .AddProjects(extensionConfiguration)
            .AddExtensionHostCapability<IProjectDefaultResolver>()
            .AddExtensionHostCapability<IExtensionCallerAccessor>()
            .AddSingleton<IExtensionServerAddress, ExtensionServerAddress>()
            .AddExtensionHostCapability<IExtensionServerAddress>();
        configureHostServices?.Invoke(serviceCollection);

        ServiceProvider services = serviceCollection.BuildServiceProvider();
        var host = new ExtensionRuntimeIntegrationHost(connection, services, paths, targetPackageDirectory);
        ExtensionPackageCatalog catalog = services.GetRequiredService<ExtensionPackageCatalog>();
        await catalog.InitializeAsync(cancellationToken);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        AgwDbContext db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await scope
            .ServiceProvider.GetRequiredService<ISettingsStore>()
            .TryCreateGlobalAsync(
                ExtensionDeploymentSettings.Key,
                new ExtensionDeploymentSettings { ServerId = Guid.CreateVersion7().ToString() },
                cancellationToken
            );
        await scope
            .ServiceProvider.GetRequiredService<IExtensionEnablementStore>()
            .SetAsync(Guid.Parse(manifest.RootElement.GetProperty("id").GetString()!), true);
        return host;
    }

    public async Task<ExtensionGenerationSnapshot> LoadAndActivateAsync(
        Guid extensionId,
        long epoch = 1,
        bool resumeBackground = true,
        CancellationToken cancellationToken = default
    )
    {
        ExtensionLocalPackage package = _services
            .GetRequiredService<ExtensionPackageCatalog>()
            .GetValidPackage(extensionId);
        ExtensionServerManifest server =
            package.Manifest.Server ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        ExtensionGenerationSnapshot generation = await Runtime.LoadAsync(
            new ExtensionRuntimeLoadRequest
            {
                ExtensionId = extensionId,
                Name = package.Manifest.Name,
                Version = package.Manifest.Version,
                ReleaseHash = package.ReleaseHash,
                Epoch = epoch,
                DataDirectory = DataDirectory(extensionId),
                PackageDirectory = package.SnapshotDirectory,
                EntryAssemblyPath = server.EntryAssembly,
                EntryType = server.EntryType,
                EventPermissions = server.Permissions.Events,
            },
            cancellationToken
        );
        await Runtime.PrepareDatabaseAsync(generation.GenerationId, cancellationToken);
        await Runtime.ActivateAsync(generation.GenerationId, cancellationToken);
        if (resumeBackground)
            await Runtime.ResumeAsync(generation.GenerationId, cancellationToken);
        return generation;
    }

    public async Task<ExtensionRuntimeIntegrationResponse> InvokeEndpointAsync(
        Guid extensionId,
        string endpointId,
        JsonElement request,
        CancellationToken cancellationToken = default
    )
    {
        await using IExtensionGenerationLease lease = await Runtime.AcquireCurrentAsync(extensionId, cancellationToken);
        await using IExtensionInvocationResponse response = await Runtime.InvokeEndpointAsync(
            lease,
            new ExtensionEndpointInvocation
            {
                ReleaseHash = lease.Pin.ReleaseHash,
                Epoch = lease.Pin.Epoch,
                EndpointId = endpointId,
                Request = request,
            },
            cancellationToken
        );
        byte[]? body = null;
        if (response.BodyStream is { } stream)
        {
            await using var output = new MemoryStream();
            await foreach (ReadOnlyMemory<byte> segment in stream.WithCancellation(cancellationToken))
                await output.WriteAsync(segment, cancellationToken);
            body = output.ToArray();
        }
        await response.CompleteAsync(cancellationToken);
        return new ExtensionRuntimeIntegrationResponse(response.StatusCode, response.JsonBody?.Clone(), body);
    }

    public async ValueTask<ExtensionEventHandlerResult> InvokeEventHandlerAsync(
        Guid extensionId,
        ExtensionEventHandlerInvocation invocation,
        CancellationToken cancellationToken = default
    )
    {
        await using IExtensionGenerationLease lease = await Runtime.AcquireCurrentAsync(extensionId, cancellationToken);
        return await Runtime.InvokeEventHandlerAsync(lease, invocation, cancellationToken);
    }

    public async Task StopGenerationAsync(Guid extensionId, CancellationToken cancellationToken = default)
    {
        ExtensionGenerationSnapshot generation = Runtime
            .GetSnapshots()
            .Single(snapshot => snapshot.GenerationId.ExtensionId == extensionId);
        if (!await Runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(5), cancellationToken))
            throw new AgwException(ErrorCodes.ExtensionOperationConflict);
        await Runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Runtime.DisposeAsync();
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
        if (Directory.Exists(_paths.Root))
            Directory.Delete(_paths.Root, recursive: true);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
        }

        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Agw.Extensions.TestSupport";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed record ExtensionRuntimeIntegrationResponse(int StatusCode, JsonElement? JsonBody, byte[]? Body);
