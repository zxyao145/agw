using System.Net;
using System.Reflection;
using Agw.A2A.Extensions;
using Agw.Agents;
using Agw.Agents.Execution;
using Agw.Auth.Extensions;
using Agw.Files;
using Agw.Host.Data;
using Agw.Host.Hosting;
using Agw.Host.Middleware;
using Agw.Host.OpenApi;
using Agw.Host.Runtime;
using Agw.Infrastructure;
using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Encryption;
using Agw.Integrations.Extensions;
using Agw.Jobs;
using Agw.Projects;
using Agw.Providers;
using Agw.Settings;
using Agw.Setup.Middleware;
using Agw.Setup.Services;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Agw.Skills;
using Agw.Tools;
using Bens.Results;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Enrichers.OpenTelemetry;

namespace Agw.Host;

public static class AgwHostApplication
{
    public static async Task RunAsync(string[] args, AgwHostProfile profile, params IAgwHostModule[] modules)
    {
        var hasControlPlane = profile is AgwHostProfile.ControlPlane or AgwHostProfile.Standalone;
        var hasDataPlane = profile is AgwHostProfile.DataPlane or AgwHostProfile.Standalone;
        var contentRootPath = AppContext.BaseDirectory;

        if (args.Length == 1 && string.Equals(args[0], "serve", StringComparison.OrdinalIgnoreCase))
        {
            args = [];
        }

        if (
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
            && !string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                "true",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", LocalServerEndpointResolver.ResolveDefaultUrl());
        }

        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { Args = args, ContentRootPath = contentRootPath }
        );
        var dataPaths = AgwDataPaths.ResolveFromConfiguration(builder.Configuration);
        dataPaths.EnsureCreated();

        // Configure Serilog early in the pipeline
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(
                new ConfigurationBuilder()
                    .SetBasePath(contentRootPath)
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .AddJsonFile(
                        $"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json",
                        optional: true,
                        reloadOnChange: true
                    )
                    .Build()
            )
            // Keep framework protocol errors visible for configuration and callback
            // diagnostics; Agw.Auth.Oidc also emits bounded stage/category details.
            .MinimumLevel.Override(
                "Microsoft.AspNetCore.Authentication.OpenIdConnect",
                Serilog.Events.LogEventLevel.Error
            )
            .MinimumLevel.Override("Microsoft.AspNetCore.Authentication.OAuth", Serilog.Events.LogEventLevel.Error)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.WithOpenTelemetryTraceId()
            .Enrich.WithOpenTelemetrySpanId()
            .WriteTo.Async(sink =>
                sink.File(
                    Path.Combine(dataPaths.LogsDirectory, $"application-{profile.ToString().ToLowerInvariant()}-.log"),
                    rollingInterval: RollingInterval.Hour,
                    retainedFileCountLimit: 30,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1)
                )
            )
            .CreateLogger();

        try
        {
            Log.Information("Starting Agw {HostProfile} Host", profile);

            ServerDeploymentConfiguration.Apply(builder.Configuration);
            ServerDeploymentConfiguration.Validate(profile, builder.Configuration);
            builder.Services.AddSingleton(dataPaths);
            builder.Services.AddSingleton(TimeProvider.System);
            builder
                .Services.AddDataProtection()
                .ConfigureAgwApplication()
                .PersistKeysToFileSystem(new DirectoryInfo(dataPaths.KeysDirectory));

            // Use Serilog for logging
            builder.Host.UseSerilog();

            // Configure OpenTelemetry
            var serviceName = builder.Configuration.GetValue<string>("OpenTelemetry:ServiceName") ?? $"Agw.{profile}";
            if (profile != AgwHostProfile.Standalone && string.Equals(serviceName, "Agw", StringComparison.Ordinal))
            {
                serviceName = $"Agw.{profile}";
            }
            var serviceVersion = builder.Configuration.GetValue<string>("OpenTelemetry:ServiceVersion") ?? "1.0.0";
            var configuredOtlpEndpoint = builder.Configuration.GetValue<string>("OpenTelemetry:OtlpEndpoint");
            var otlpEndpoint = new Uri(
                string.IsNullOrWhiteSpace(configuredOtlpEndpoint) ? "http://localhost:4317" : configuredOtlpEndpoint
            );

            builder
                .Services.AddOpenTelemetry()
                .ConfigureResource(resource =>
                    resource.AddService(serviceName: serviceName, serviceVersion: serviceVersion)
                )
                .WithTracing(tracing =>
                    tracing
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddEntityFrameworkCoreInstrumentation(options =>
                        {
                            options.EnrichWithIDbCommand = (activity, command) =>
                            {
                                activity.SetTag("db.command.text", command.CommandText);
                            };
                        })
                        .AddSource("Agw.*")
                        .AddSource("Microsoft.Agents.AI.Workflows")
                        .AddOtlpExporter(options =>
                        {
                            options.Endpoint = otlpEndpoint;
                        })
                )
                .WithMetrics(metrics =>
                    metrics
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddMeter("Agw.*")
                        .AddOtlpExporter(options =>
                        {
                            options.Endpoint = otlpEndpoint;
                        })
                );

            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
                logging.AddOtlpExporter(options =>
                {
                    options.Endpoint = otlpEndpoint;
                });
            });

            if (hasControlPlane)
            {
                var mvcBuilder = builder
                    .Services.AddControllersWithViews()
                    .AddJsonOptions(options =>
                    {
                        options.JsonSerializerOptions.ReferenceHandler = System
                            .Text
                            .Json
                            .Serialization
                            .ReferenceHandler
                            .IgnoreCycles;
                        options.JsonSerializerOptions.AllowOutOfOrderMetadataProperties = true;
                    });
                foreach (var module in modules)
                {
                    module.AddApplicationParts(mvcBuilder.PartManager);
                }

                builder.Services.Configure<ApiBehaviorOptions>(options =>
                {
                    options.InvalidModelStateResponseFactory = context =>
                        ApiResult.BadRequest(context.ModelState, code: ErrorCodes.InvalidParam.Code);
                });
                builder.Services.AddEndpointsApiExplorer();
                builder.Services.AddOpenApi(options => options.AddSchemaTransformer<AgwOpenApiSchemaTransformer>());
            }
            builder.Services.AddApiResult();
            builder.Services.AddHttpClient();
            builder.Services.AddSingleton<IocUtil>();
            var allowedOrigins = builder.Configuration.GetSection("Auth:AllowedOrigins").Get<string[]>() ?? [];
            builder.Services.AddCors(options =>
            {
                options.AddPolicy(
                    "AgwDesktop",
                    policy =>
                    {
                        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
                    }
                );
            });
            if (hasDataPlane)
            {
                builder.Services.AddSignalR(options =>
                {
                    options.MaximumReceiveMessageSize = 16 * 1024 * 1024;
                });
            }
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders =
                    ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = 1;
                var configuredProxies =
                    builder.Configuration.GetSection("ReverseProxy:TrustedProxies").Get<string[]>() ?? [];
                foreach (var configuredProxy in configuredProxies)
                {
                    if (IPAddress.TryParse(configuredProxy, out var address))
                        options.KnownProxies.Add(address);
                }
            });
            builder.Services.AddHealthChecks();

            // add module
            var moduleServices = builder.Services;
            if (hasDataPlane)
            {
                moduleServices.AddA2A(builder.Configuration);
            }
            moduleServices
                .AddTools(builder.Configuration)
                .AddAgents(builder.Configuration)
                .AddAgentExecution(
                    builder.Configuration,
                    new Agw.Agents.Execution.DependencyInjection.RegistrationOptions(
                        AddExecutionTransport: hasDataPlane,
                        AddDistributedWorker: hasDataPlane,
                        AddTraceCollector: hasDataPlane,
                        AddRuntime: hasDataPlane
                    )
                )
                .AddFiles(builder.Configuration)
                .AddInfrastructure(builder.Configuration)
                .AddJobs(
                    builder.Configuration,
                    new Agw.Jobs.DependencyInjection.RegistrationOptions(
                        AddScheduler: hasControlPlane,
                        UseDurableExecution: string.Equals(
                            builder.Configuration["Execution:Provider"],
                            "Distributed",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                )
                .AddProviders(builder.Configuration)
                .AddSkills(builder.Configuration)
                .AddProjects(builder.Configuration)
                .AddAuth()
                .AddSettings()
                .AddSetup(builder.Configuration, readOnly: profile == AgwHostProfile.DataPlane)
                .AddIntegrations(builder.Configuration);

            if (hasControlPlane)
                builder.Services.AddOidcAuthentication(builder.Configuration, builder.Environment);

            // 数据库 AuditUserId 提供者
            builder.Services.AddScoped<IEntityAuditUserIdProvider, EntityAuditUserIdProvider>();

            builder.Services.AddHybridCache();

            var app = builder.Build();
            var stateStore = app.Services.GetRequiredService<DatabaseInitializationStateStore>();
            await stateStore.RefreshAsync();
            if (hasControlPlane && await ServerCommand.TryRunAsync(args, stateStore))
                return;
            _ = app.Services.GetRequiredService<ToolRegistryService>();
            var databaseSettings = app.Services.GetRequiredService<IOptions<DatabaseSettings>>().Value;
            Log.Information("Database provider: {DatabaseProvider}", databaseSettings.Provider);
            var databaseConnectionString = DatabaseConnectionStringResolver.Resolve(
                databaseSettings.Provider,
                databaseSettings.ConnectionString,
                dataPaths
            );
            Log.Debug(
                "Database connection string: {DatabaseConnectionString}",
                DatabaseConnectionStringResolver.ToSafeLogValue(databaseConnectionString)
            );
            var iocUtil = app.Services.GetRequiredService<IocUtil>();

            // Apply configured first-run setup, or seed an already initialized database on the Control Plane.
            if (hasControlPlane)
            {
                using var scope = app.Services.CreateScope();
                var configuredSetupInitializer = scope.ServiceProvider.GetRequiredService<ConfiguredSetupInitializer>();
                var initializedFromConfiguration = await configuredSetupInitializer.InitializeIfConfiguredAsync();
                var initializationState = scope.ServiceProvider.GetRequiredService<IServerInitializationState>();
                if (initializationState.IsInitialized)
                {
                    await using var initializationLock = await scope
                        .ServiceProvider.GetRequiredService<IApplicationLock>()
                        .AcquireAsync("host-startup-initialization", CancellationToken.None);
                    if (!initializedFromConfiguration)
                    {
                        var seeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
                        await seeder.SeedAsync();
                    }
                }
            }

            if (hasControlPlane && app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
                app.MapScalarApiReference();
            }

            app.UseForwardedHeaders();
            app.UseMiddleware<TraceIdResponseHeaderMiddleware>();

            // Add Serilog request logging
            app.UseSerilogRequestLogging(options =>
            {
                options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
                {
                    diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
                    diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
                };
            });
            app.UseMiddleware<ClientClosedRequestMiddleware>();

            app.UseCors("AgwDesktop");
            if (hasControlPlane)
            {
                app.UseMiddleware<InitializationGuardMiddleware>();
            }
            app.UseMiddleware<AgwApiExceptionMiddleware>();
            app.UseAgwAuth();
            if (hasControlPlane)
            {
                app.UseDefaultFiles();
                app.UseStaticFiles();
            }
            app.UseRouting();
            app.UseAuthorization();

            foreach (var module in modules)
            {
                module.MapEndpoints(app);
            }
            app.MapGet("/api/health/live", () => Results.Ok(new { status = "live", hostProfile = profile.ToString() }));
            app.MapGet(
                "/api/health/ready",
                async (IServerInitializationState initializationState, AgwDbContext dbContext) =>
                {
                    if (!initializationState.IsInitialized || !await dbContext.Database.CanConnectAsync())
                    {
                        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                    }
                    return Results.Ok(new { status = "ready", hostProfile = profile.ToString() });
                }
            );
            if (hasControlPlane)
            {
                app.MapFallbackToFile("404.html");
            }

            if (hasControlPlane && !app.Services.GetRequiredService<IServerInitializationState>().IsInitialized)
            {
                var setupCodeService = app.Services.GetRequiredService<SetupCodeService>();
                Log.Warning("Agw remote setup code: {SetupCode}", setupCodeService.CurrentCode);
            }

            Log.Information("Agw Host configured successfully");

            if (app.Services.GetRequiredService<IServerInitializationState>().IsInitialized)
            {
                await using var upgradeScope = app.Services.CreateAsyncScope();
                await upgradeScope
                    .ServiceProvider.GetRequiredService<Agw.Infrastructure.Agents.DurableTurnUpgrade>()
                    .UpgradeAsync(CancellationToken.None);
                if (
                    app.Services.GetRequiredService<
                        IOptions<Agw.Agents.Execution.Configuration.ExecutionRuntimeOptions>
                    >().Value.Provider == Agw.Agents.Contracts.Execution.ExecutionProvider.InProcess
                )
                {
                    await upgradeScope
                        .ServiceProvider.GetRequiredService<Agw.Infrastructure.Projects.InProcessTurnRecovery>()
                        .RecoverAsync(CancellationToken.None);
                }
            }

            await app.StartAsync();
            ServerRuntimeDescriptorStore? runtimeStore = null;
            if (profile == AgwHostProfile.Standalone)
            {
                var server = app.Services.GetRequiredService<IServer>();
                var serverAddresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? app.Urls;
                var serverAddress =
                    serverAddresses
                        .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri : null)
                        .FirstOrDefault(uri => uri is { Scheme: "http" or "https" })
                    ?? new Uri(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")!.Split(';')[0]);
                var publicHost = serverAddress.Host is "0.0.0.0" or "[::]" or "::" ? "127.0.0.1" : serverAddress.Host;
                var publicUrl = new UriBuilder(serverAddress) { Host = publicHost }.Uri.GetLeftPart(
                    UriPartial.Authority
                );
                var serverVersion =
                    Assembly
                        .GetEntryAssembly()
                        ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                        ?.InformationalVersion
                    ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                    ?? "0.0.0-dev";
                runtimeStore = new ServerRuntimeDescriptorStore(dataPaths);
                await runtimeStore.WriteAsync(
                    new ServerRuntimeDescriptor(
                        SchemaVersion: 1,
                        Pid: Environment.ProcessId,
                        BaseUrl: publicUrl,
                        Port: serverAddress.Port,
                        ServerVersion: serverVersion,
                        ApiMajorVersion: 1,
                        StartedAt: app.Services.GetRequiredService<TimeProvider>().GetUtcNow()
                    )
                );
            }

            try
            {
                await app.WaitForShutdownAsync();
            }
            finally
            {
                if (runtimeStore != null)
                {
                    await runtimeStore.DeleteIfOwnedAsync(Environment.ProcessId);
                }
            }
        }
        catch (HostAbortedException hae)
        {
            Log.Warning(hae, "HostAbortedException");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Agw Host terminated unexpectedly");
            Environment.ExitCode = 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
