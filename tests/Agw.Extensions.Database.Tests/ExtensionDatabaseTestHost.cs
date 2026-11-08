using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Extensions.Application.Packages;
using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Packages;
using Agw.Extensions.Infrastructure.Runtime;
using Agw.Extensions.Sdk.Database;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Host.Data;
using Agw.Infrastructure;
using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Infrastructure.Settings;
using Agw.Settings;
using Agw.Settings.Application.Persistence;
using Agw.Shared.Configuration;
using Agw.Shared.Contracts;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Agw.Extensions.Database.Tests;

internal sealed class ExtensionDatabaseTestHost : IAsyncDisposable
{
    private const string EntryAssemblyPath = "server/Agw.Extensions.Runtime.TestExtension.dll";
    private const string EntryType = "Agw.Extensions.Runtime.Tests.Fixture.DatabaseTestExtension";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ServiceProvider _services;
    private readonly string _dataDirectory;
    private readonly string _dataRoot;

    private ExtensionDatabaseTestHost(
        ServiceProvider services,
        string dataRoot,
        string dataDirectory,
        ExtensionDatabaseProvider provider,
        string connectionString
    )
    {
        _services = services;
        _dataRoot = dataRoot;
        _dataDirectory = dataDirectory;
        Provider = provider;
        ConnectionString = connectionString;
    }

    public ExtensionRuntime Runtime => _services.GetRequiredService<ExtensionRuntime>();

    public IServiceProvider Services => _services;

    public string DataDirectory => _dataDirectory;

    public ExtensionDatabaseProvider Provider { get; }

    public string ConnectionString { get; }

    public static async Task<ExtensionDatabaseTestHost> CreateAsync(
        ExtensionDatabaseMigrationTests.MigrationTestDatabase database,
        bool initiallyEnabled = true,
        string packageVersion = "1.0.0"
    )
    {
        string dataRoot = Path.Combine(AppContext.BaseDirectory, $"extension-database-test-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(dataRoot);
        AgwDataPaths paths = AgwDataPaths.Resolve(dataRoot, AppContext.BaseDirectory);
        Directory.CreateDirectory(paths.Root);
        string extensionDirectory = Path.Combine(paths.Root, "extension-source");
        await CreateExtensionPackageAsync(extensionDirectory, packageVersion, TestContext.Current.CancellationToken);

        var services = new ServiceCollection();
        IHostEnvironment environment = Microsoft
            .Extensions.Hosting.Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings
                {
                    ContentRootPath = AppContext.BaseDirectory,
                    DisableDefaults = true,
                }
            )
            .Environment;
        services
            .AddLogging(logging =>
                logging
                    .AddConsole()
                    .SetMinimumLevel(LogLevel.Warning)
                    .AddFilter("Microsoft.EntityFrameworkCore.Migrations", LogLevel.Information)
                    .AddFilter("Agw.Extensions.Infrastructure.Runtime.ExtensionRuntime", LogLevel.Information)
            )
            .AddSingleton<IHostEnvironment>(environment)
            .AddSingleton(paths)
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .Configure<DatabaseSettings>(settings =>
            {
                settings.Provider =
                    database.Provider == ExtensionDatabaseProvider.Sqlite
                        ? DatabaseProvider.Sqlite
                        : DatabaseProvider.Postgres;
                settings.ConnectionString = database.ConnectionString;
            })
            .AddScoped<ICurrentUser, UserInfoService>()
            .AddScoped<IUserInfoService, UserInfoService>()
            .AddScoped<IEntityAuditUserIdProvider, EntityAuditUserIdProvider>()
            .AddScoped<EntityCreatorInterceptor>()
            .AddScoped<EntityModifierInterceptor>()
            .AddScoped<ISettingsPersistence, EfSettingsPersistence>()
            .AddScoped<ISettingsDbContext>(provider => provider.GetRequiredService<AgwDbContext>())
            .AddDbContext<AgwDbContext>(
                (provider, options) =>
                {
                    if (database.Provider == ExtensionDatabaseProvider.Sqlite)
                    {
                        options.UseSqlite(database.ConnectionString);
                    }
                    else
                    {
                        options.UseNpgsql(database.ConnectionString);
                    }

                    options
                        .UseSnakeCaseNamingConvention()
                        .AddInterceptors(
                            provider.GetRequiredService<EntityCreatorInterceptor>(),
                            provider.GetRequiredService<EntityModifierInterceptor>()
                        );
                }
            )
            .AddSettings()
            .AddExtensionInfrastructure()
            .AddExtensions(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?> { ["Extensions:Directory"] = extensionDirectory }
                    )
                    .Build(),
                "DataPlane"
            );
        services.AddScoped<DbContext>(provider => provider.GetRequiredService<AgwDbContext>());

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        var host = new ExtensionDatabaseTestHost(
            serviceProvider,
            paths.Root,
            Path.Combine(paths.Root, "extensions", "data", ExtensionId.ToString("N")),
            database.Provider,
            database.ConnectionString
        );
        try
        {
            await serviceProvider
                .GetRequiredService<ExtensionPackageCatalog>()
                .InitializeAsync(TestContext.Current.CancellationToken);
            await using var scope = serviceProvider.CreateAsyncScope();
            AgwDbContext db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            if (initiallyEnabled)
            {
                await scope
                    .ServiceProvider.GetRequiredService<IExtensionEnablementStore>()
                    .SetAsync(ExtensionId, true, TestContext.Current.CancellationToken);
            }
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async Task<ExtensionGenerationSnapshot> LoadGenerationAsync(CancellationToken cancellationToken = default)
    {
        ExtensionLocalPackage package = GetPackage();
        ExtensionEnablementState enablement = await GetEnablementStateAsync(cancellationToken);
        return await Runtime.LoadAsync(
            new ExtensionRuntimeLoadRequest
            {
                ExtensionId = ExtensionId,
                Name = package.Manifest.Name,
                Version = package.Manifest.Version,
                ReleaseHash = package.ReleaseHash,
                Epoch = enablement.Version,
                DataDirectory = _dataDirectory,
                PackageDirectory = package.SnapshotDirectory,
                EntryAssemblyPath = package.Manifest.Server!.EntryAssembly,
                EntryType = package.Manifest.Server.EntryType,
            },
            cancellationToken
        );
    }

    public async Task<ExtensionGenerationSnapshot> LoadAndActivateAsync(CancellationToken cancellationToken = default)
    {
        ExtensionGenerationSnapshot generation = await LoadGenerationAsync(cancellationToken);
        await Runtime.PrepareDatabaseAsync(generation.GenerationId, cancellationToken);
        await Runtime.ActivateAsync(generation.GenerationId, cancellationToken);
        await Runtime.ResumeAsync(generation.GenerationId, cancellationToken);
        return generation;
    }

    public async Task<ExtensionDatabaseInvocation> BeginEndpointInvocationAsync(
        string? userId,
        string endpointId,
        object? body,
        CancellationToken cancellationToken = default
    )
    {
        IDisposable userScope = UserInfoUtil.Push(CreatePrincipal(userId));
        IExtensionGenerationLease? lease = null;
        try
        {
            lease = await Runtime.AcquireCurrentAsync(ExtensionId, cancellationToken);
            JsonElement? requestBody = body is null
                ? null
                : JsonSerializer.SerializeToElement(body, body.GetType(), ManifestJsonOptions);
            JsonElement request = JsonSerializer.SerializeToElement(
                new
                {
                    method = "POST",
                    query = new Dictionary<string, IReadOnlyList<string>>(),
                    body = requestBody,
                },
                ManifestJsonOptions
            );
            IExtensionInvocationResponse response = await Runtime.InvokeEndpointAsync(
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
            return new ExtensionDatabaseInvocation(lease, response, userScope);
        }
        catch
        {
            userScope.Dispose();
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            throw;
        }
    }

    public async Task<JsonElement?> InvokeEndpointAsync(
        string? userId,
        string endpointId,
        object? body,
        CancellationToken cancellationToken = default
    )
    {
        await using ExtensionDatabaseInvocation invocation = await BeginEndpointInvocationAsync(
            userId,
            endpointId,
            body,
            cancellationToken
        );
        JsonElement? response = invocation.JsonBody?.Clone();
        await invocation.CompleteAsync(cancellationToken);
        return response;
    }

    public async Task<ExtensionEnablementState> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default
    )
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope
            .ServiceProvider.GetRequiredService<IExtensionEnablementStore>()
            .SetAsync(ExtensionId, enabled, cancellationToken);
    }

    public ExtensionDatabaseFactoryFixture CreateSdkGeneration<TContext, TMigrationsAssemblyMarker>(
        ExtensionDatabaseOptions options
    )
        where TContext : ExtensionDbContext
        where TMigrationsAssemblyMarker : class
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IExtensionUserContext>(new ExtensionUserContext());
        services.AddSingleton(_services.GetRequiredService<IExtensionEpochValidator>());
        services.AddSingleton(_services.GetRequiredService<IExtensionSchemaEpochValidator>());
        services.AddSingleton<IExtensionDatabaseTransactionCoordinator>(
            _services.GetRequiredService<IExtensionDatabaseTransactionCoordinator>()
        );
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        var factory = new ExtensionDatabaseFactory<TContext, TMigrationsAssemblyMarker>();
        factory.ConfigureServices(services, options);
        ServiceProvider serviceProvider = services.BuildServiceProvider();
        return new ExtensionDatabaseFactoryFixture(serviceProvider, factory.Create(serviceProvider, options));
    }

    private async Task<ExtensionEnablementState> GetEnablementStateAsync(CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope
            .ServiceProvider.GetRequiredService<IExtensionEnablementStore>()
            .GetAsync(ExtensionId, cancellationToken);
    }

    private ExtensionLocalPackage GetPackage() =>
        _services.GetRequiredService<ExtensionPackageCatalog>().GetValidPackage(ExtensionId);

    private static ClaimsPrincipal CreatePrincipal(string? userId) =>
        userId is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "extension-database-test")
            );

    private static async Task CreateExtensionPackageAsync(
        string directory,
        string version,
        CancellationToken cancellationToken
    )
    {
        string sourceDirectory = Path.Combine(AppContext.BaseDirectory, "ExtensionFixture");
        string packageDirectory = Path.Combine(directory, ExtensionId.ToString("D"));
        string serverDirectory = Path.Combine(packageDirectory, "server");
        Directory.CreateDirectory(serverDirectory);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(sourceDirectory, sourcePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            string targetPath = Path.Combine(serverDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            byte[] contents = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            await File.WriteAllBytesAsync(targetPath, contents, cancellationToken);
            files.Add($"server/{relativePath}", Convert.ToHexStringLower(SHA256.HashData(contents)));
        }

        string manifest = JsonSerializer.Serialize(
            new
            {
                manifestVersion = 2,
                id = ExtensionId.ToString(),
                name = "database-test",
                displayName = "Database Test",
                version,
                targets = (int)ExtensionTarget.Server,
                server = new
                {
                    apiVersion = 1,
                    entryAssembly = EntryAssemblyPath,
                    entryType = EntryType,
                },
                files,
            },
            ManifestJsonOptions
        );
        await File.WriteAllBytesAsync(
            Path.Combine(packageDirectory, "manifest.json"),
            Encoding.UTF8.GetBytes(manifest),
            cancellationToken
        );
    }

    public async ValueTask DisposeAsync()
    {
        await Runtime.DisposeAsync();
        await _services.DisposeAsync();
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    internal static Guid ExtensionId { get; } = Guid.ParseExact("019a4c175f007a808b3d2b732f48a100", "N");
}

internal sealed class ExtensionDatabaseFactoryFixture(
    ServiceProvider serviceProvider,
    IExtensionDatabaseGeneration generation
) : IAsyncDisposable
{
    public IServiceProvider Services => serviceProvider;

    public IExtensionDatabaseGeneration Generation => generation;

    public async ValueTask DisposeAsync()
    {
        await generation.DisposeAsync();
        await serviceProvider.DisposeAsync();
    }
}

internal sealed class ExtensionDatabaseInvocation(
    IExtensionGenerationLease lease,
    IExtensionInvocationResponse response,
    IDisposable userScope
) : IAsyncDisposable
{
    public JsonElement? JsonBody => response.JsonBody;

    public IAsyncEnumerable<ReadOnlyMemory<byte>>? BodyStream => response.BodyStream;

    public int StatusCode => response.StatusCode;

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default) =>
        response.CompleteAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await response.DisposeAsync();
        }
        finally
        {
            try
            {
                await lease.DisposeAsync();
            }
            finally
            {
                userScope.Dispose();
            }
        }
    }
}
