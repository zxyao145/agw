using A2A;
using DSystem.A2A;
using DSystem.Api.Controllers;
using DSystem.Domain.Services;
using DSystem.ExternalAgents;
using DSystem.Host;
using DSystem.Infrastructure;
using DSystem.Manager.Api.Controllers;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Enrichers.OpenTelemetry;
using System.Configuration;
using System.Runtime;

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
    .CreateLogger();

try
{
    Log.Information("Starting D-System Host");

    var builder = WebApplication.CreateBuilder(args);

    // Use Serilog for logging
    builder.Host.UseSerilog();

    // Configure OpenTelemetry
    var serviceName = builder.Configuration.GetValue<string>("OpenTelemetry:ServiceName") ?? "DSystem";
    var serviceVersion = builder.Configuration.GetValue<string>("OpenTelemetry:ServiceVersion") ?? "1.0.0";

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource
            .AddService(serviceName: serviceName, serviceVersion: serviceVersion))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation(options =>
            {
                options.SetDbStatementForText = true;
                options.EnrichWithIDbCommand = (activity, command) =>
                {
                    activity.SetTag("db.command.text", command.CommandText);
                };
            })
            .AddSource("DSystem.*")
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(builder.Configuration.GetValue<string>("OpenTelemetry:OtlpEndpoint") ?? "http://localhost:4317");
            }))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter("DSystem.*")
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
        .AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        })
        .AddApplicationPart(typeof(AgentsController).Assembly)
        .AddApplicationPart(typeof(ProjectsController).Assembly);
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddOpenApi();

    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddSingleton<ToolRegistryService>();  // Singleton to cache tool discovery
    builder.Services.AddScoped<ModelDomainService>();
    builder.Services.AddScoped<ProviderDomainService>();
    builder.Services.AddScoped<ModelProviderDomainService>();
    builder.Services.AddScoped<ModelProviderApiKeyDomainService>();
    builder.Services.AddScoped<AgentDomainService>();
    builder.Services.AddScoped<AgentRuntimeService>();
    builder.Services.AddScoped<A2AAgentService>();

    // External Agents
    builder.Services.AddScoped<ClaudeCodeService>();

    builder.Services.AddScoped<AgentflowDomainService>();
    builder.Services.AddScoped<AgentflowRuntimeService>();
    builder.Services.AddScoped<IAgentflowAgentExecutor, PlaceholderAgentflowAgentExecutor>();

    builder.Services.AddScoped<ProjectDomainService>();
    builder.Services.AddScoped<ProjectTaskDomainService>();
    builder.Services.AddHostedService<ProjectTaskSchedulerHostedService>();
    builder.Services.AddHybridCache();

    builder.Services.Configure<A2AServerOptions>(o=>
    {

    });

    builder.Services.AddSingleton<TaskManagerFactory>(sp =>
    {
        return new TaskManagerFactory(sp);
    });

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }
    else
    {
        app.UseHttpsRedirection();
    }

    // Add Serilog request logging
    app.UseSerilogRequestLogging(options =>
    {
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
            diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
        };
    });

    // Enable WebSocket support
    app.UseWebSockets();

    var a2AServerOptions = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<A2AServerOptions>>()
        .Value;
    app.MapDSystemA2A(a2AServerOptions.Prefix);
    app.MapControllers();

    Log.Information("D-System Host configured successfully");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "D-System Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
