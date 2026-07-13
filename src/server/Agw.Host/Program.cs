using System.Net;

using Agw.A2A;
using Agw.A2A.Extensions;
using Agw.Agents;
using Agw.Agents.Definitions.Controllers;
using Agw.Agents.Execution.Transport.SignalR;
using Agw.Files;
using Agw.Files.Controllers;
using Agw.Infrastructure;
using Agw.Infrastructure.Data;
using Agw.Integrations.Controllers;
using Agw.Integrations.Extensions;
using Agw.Jobs;
using Agw.Jobs.Controllers;
using Agw.Manager.Api.Controllers;
using Agw.Host.Controllers;
using Agw.Host.Middleware;
using Agw.Providers;
using Agw.Setup.Controllers;
using Agw.Setup.Middleware;
using Agw.Setup.Services;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Agw.Skills;
using Agw.Skills.Controllers;
using Agw.Tasks;
using Agw.Tasks.Controllers;
using Agw.Tools;

using Bens.Results;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.OpenApi;

using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using Scalar.AspNetCore;

using Serilog;
using Serilog.Enrichers.OpenTelemetry;

var dataPaths = AgwDataPaths.ResolveFromEnvironment();
dataPaths.EnsureCreated();

if (args.Length == 1 && string.Equals(args[0], "serve", StringComparison.OrdinalIgnoreCase))
{
    args = [];
}

if (await Agw.Host.ServerCommand.TryRunAsync(args, dataPaths))
{
    return;
}

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    && !string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
{
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:5015");
}

// Configure Serilog early in the pipeline
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true, reloadOnChange: true)
        .Build())
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .Enrich.WithOpenTelemetryTraceId()
    .Enrich.WithOpenTelemetrySpanId()
    .WriteTo.Async(sink => sink.File(
        Path.Combine(dataPaths.LogsDirectory, "application-.log"),
        rollingInterval: RollingInterval.Hour,
        retainedFileCountLimit: 30,
        shared: true,
        flushToDiskInterval: TimeSpan.FromSeconds(1)))
    .CreateLogger();

try
{
    Log.Information("Starting Agw Host");

    var builder = WebApplication.CreateBuilder(args);

    builder.Configuration.AddJsonFile(dataPaths.StateFile, optional: true, reloadOnChange: true);
    builder.Services.AddSingleton(dataPaths);
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dataPaths.KeysDirectory));

    // Use Serilog for logging
    builder.Host.UseSerilog();

    // Configure OpenTelemetry
    var serviceName = builder.Configuration.GetValue<string>("OpenTelemetry:ServiceName") ?? "Agw";
    var serviceVersion = builder.Configuration.GetValue<string>("OpenTelemetry:ServiceVersion") ?? "1.0.0";

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource
            .AddService(serviceName: serviceName, serviceVersion: serviceVersion))
        .WithTracing(tracing => tracing
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
                options.Endpoint = new Uri(builder.Configuration.GetValue<string>("OpenTelemetry:OtlpEndpoint") ?? "http://localhost:4317");
            }))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter("Agw.*")
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(builder.Configuration.GetValue<string>("OpenTelemetry:OtlpEndpoint") ?? "http://localhost:4317");
            }));

    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.IncludeFormattedMessage = true;
        logging.IncludeScopes = true;
        logging.AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(builder.Configuration.GetValue<string>("OpenTelemetry:OtlpEndpoint") ?? "http://localhost:4317");
        });
    });

    builder.Services
        .AddControllersWithViews()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        })
        .AddApplicationPart(typeof(AgentsController).Assembly)
        .AddApplicationPart(typeof(ProjectsController).Assembly)
        .AddApplicationPart(typeof(FilesController).Assembly)
        .AddApplicationPart(typeof(SkillsController).Assembly)
        .AddApplicationPart(typeof(JobsController).Assembly)
        .AddApplicationPart(typeof(SetupController).Assembly)
        .AddApplicationPart(typeof(OAuthController).Assembly)
        .AddApplicationPart(typeof(ToolsController).Assembly)
        ;
    builder.Services.Configure<ApiBehaviorOptions>(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
            ApiResult.BadRequest(
                context.ModelState,
                code: ErrorCodes.InvalidParam.Code);
    });
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddOpenApi(options =>
    {
        options.AddSchemaTransformer((schema, context, cancellationToken) =>
        {
            if (context.JsonTypeInfo.Type == typeof(int) ||
                context.JsonTypeInfo.Type == typeof(int?))
            {
                schema.Type = JsonSchemaType.Integer;
                schema.Format = "int32";
                schema.Pattern = null;
            }

            var type = context.JsonTypeInfo.Type;
            if (type.IsClass)
            {
                foreach (var property in context.JsonTypeInfo.Properties)
                {
                    var propertyType = property.PropertyType;

                    // 非 nullable value type，例如 int、long、bool、DateTime
                    if ((propertyType.IsValueType || propertyType == typeof(string)) &&
                        Nullable.GetUnderlyingType(propertyType) is null)
                    {
                        var jsonName = property.Name;

                        schema.Required ??= new HashSet<string>();
                        schema.Required.Add(jsonName);
                    }
                }
            }


            return Task.CompletedTask;
        });
    });
    builder.Services.AddApiResult();
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<IocUtil>();
    builder.Services.AddAntiforgery(options =>
    {
        options.HeaderName = "X-CSRF-TOKEN";
        options.Cookie.Name = "agw.antiforgery";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
    builder.Services.AddAuthentication(AuthController.CookieScheme)
        .AddCookie(AuthController.CookieScheme, options =>
        {
            options.Cookie.Name = "agw.session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
            options.SlidingExpiration = true;
            options.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                },
                OnValidatePrincipal = context =>
                {
                    var store = context.HttpContext.RequestServices.GetRequiredService<IInitializationStateStore>();
                    var expected = store.GetSnapshot().SessionVersion.ToString();
                    if (!string.Equals(context.Principal?.FindFirst("session_version")?.Value, expected, StringComparison.Ordinal))
                    {
                        context.RejectPrincipal();
                    }
                    return Task.CompletedTask;
                }
            };
        });
    builder.Services.AddAuthorization();
    builder.Services.AddSignalR(options =>
    {
        options.MaximumReceiveMessageSize = 64 * 1024;
    });
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        var configuredProxies = builder.Configuration
            .GetSection("ReverseProxy:TrustedProxies")
            .Get<string[]>() ?? [];
        foreach (var configuredProxy in configuredProxies)
        {
            if (IPAddress.TryParse(configuredProxy, out var address)) options.KnownProxies.Add(address);
        }
    });
    builder.Services.AddHealthChecks();

    // add module
    builder.Services
        .AddA2A(builder.Configuration)
        .AddAgents(builder.Configuration)
        .AddFiles(builder.Configuration)
        .AddInfrastructure(builder.Configuration)
        .AddJobs(builder.Configuration)
        .AddProviders(builder.Configuration)
        .AddSkills(builder.Configuration)
        .AddTasks(builder.Configuration)
        .AddTools(builder.Configuration)
        .AddSetup(builder.Configuration)
        .AddIntegrations(builder.Configuration)
        ;

    builder.Services.AddHybridCache();

    var app = builder.Build();
    var iocUtil = app.Services.GetRequiredService<IocUtil>();

    // Seed database on startup after initialization has been completed.
    using (var scope = app.Services.CreateScope())
    {
        var stateStore = scope.ServiceProvider.GetRequiredService<IInitializationStateStore>();
        if (stateStore.GetSnapshot().IsInitialized)
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
            await seeder.SeedAsync();
        }
    }

    if (app.Environment.IsDevelopment())
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

    app.UseMiddleware<InitializationGuardMiddleware>();
    app.UseAuthentication();
    app.UseMiddleware<AgwAuthenticationMiddleware>();
    app.UseMiddleware<AgwAntiforgeryMiddleware>();
    app.UseMiddleware<AgwAuthorizationGuardMiddleware>();
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthorization();
    app.UseMiddleware<AgwApiExceptionMiddleware>();
    app.UseMiddleware<FileEndpointExceptionMappingMiddleware>();

    var a2AServerOptions = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<AgwA2AServerOptions>>()
        .Value;
    app.MapAgwA2A(a2AServerOptions.Prefix).RequireAuthorization();
    app.MapControllers();
    app.MapHub<ExecutionHub>("/api/hubs/exec", options =>
    {
        options.Transports = HttpTransportType.WebSockets;
    }).RequireAuthorization();
    app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
    app.MapGet("/health/ready", async (IInitializationStateStore stateStore, AgwDbContext dbContext) =>
    {
        if (!stateStore.GetSnapshot().IsInitialized || !await dbContext.Database.CanConnectAsync())
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        return Results.Ok(new { status = "ready" });
    });
    app.MapFallbackToFile("404.html");

    var setupCodeService = app.Services.GetRequiredService<SetupCodeService>();
    if (!app.Services.GetRequiredService<IInitializationStateStore>().GetSnapshot().IsInitialized)
    {
        Log.Warning("Agw remote setup code: {SetupCode}", setupCodeService.CurrentCode);
    }

    Log.Information("Agw Host configured successfully");
    app.Run();
}
catch (HostAbortedException hae)
{
    Log.Warning(hae, "HostAbortedException");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Agw Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
