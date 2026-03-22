using A2A;
using Agw.A2A;
using Agw.Appliaction.Services;
using Agw.Appliaction.Services.Agents;
using Agw.Appliaction.Services.Agentflows;
using Agw.Api.Controllers;
using Agw.Domain.Services;
using Agw.Domain.Services.Agents;
using Agw.Domain.Services.Agentflows;
using Agw.Host;
using Agw.Infrastructure;
using Agw.Infrastructure.Data;
using Agw.Manager.Api.Controllers;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Enrichers.OpenTelemetry;
using System.Configuration;
using System.Runtime;
using Microsoft.Agents.AI;
using Agw.Tasks.Services;
using Agw.Skills.Services;
using Agw.Domain.Services.Skills;
using Agw.Shared.Tasks;
using Agw.Agents.ExternalAgents;

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
    Log.Information("Starting Agw Host");

    var builder = WebApplication.CreateBuilder(args);

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
        .AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        })
        .AddApplicationPart(typeof(AgentsController).Assembly)
        .AddApplicationPart(typeof(ProjectsController).Assembly)
        .AddApplicationPart(typeof(SkillsController).Assembly);
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddOpenApi();

    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddSingleton<ToolRegistryService>();  // Singleton to cache tool discovery
    builder.Services.AddSingleton<EfCoreChatHistoryProvider>();
    builder.Services.AddSingleton<ChatHistoryProvider>(sp =>
    {
        return sp.GetRequiredService<EfCoreChatHistoryProvider>();
    });
    builder.Services.AddSingleton<IProviderSessionState>(sp =>
    {
        return sp.GetRequiredService<EfCoreChatHistoryProvider>();
    });
    
    builder.Services.AddScoped<ModelDomainService>();
    builder.Services.AddScoped<ProviderDomainService>();
    builder.Services.AddScoped<ModelProviderDomainService>();
    builder.Services.AddScoped<McpToolServerDomainService>();
    builder.Services.AddScoped<AgentDomainService>();
    builder.Services.AddScoped<AgentRuntimeService>();
    builder.Services.AddScoped<A2AAgentService>();
    builder.Services.AddScoped<ITaskAppService, TaskAppService>();
    builder.Services.AddScoped<IProjectAppService, ProjectAppService>();
    builder.Services.AddScoped<ProjectTaskAppService>();
    builder.Services.AddScoped<SessionRecordAppService>();
    builder.Services.AddScoped<SkillDomainService>();
    builder.Services.AddScoped<SkillAppService>();
    
    builder.Services.AddScoped<TaskRecordDomainService>();

    // External Agents
    builder.Services.AddScoped<ClaudeCodeService>();

    builder.Services.AddScoped<AgentflowDomainService>();
    builder.Services.AddScoped<AgentflowRuntimeService>();

    builder.Services.AddScoped<ProjectDomainService>();
    builder.Services.AddScoped<ProjectTaskDomainService>();
    builder.Services.AddHostedService<ProjectTaskSchedulerHostedService>();
    builder.Services.AddHybridCache();

    builder.Services.Configure<A2AServerOptions>(o =>
    {

    });

    builder.Services.AddSingleton<TaskManagerFactory>(sp =>
    {
        return new TaskManagerFactory(sp);
    });

    // Register database seeder
    builder.Services.AddScoped<ClaudeCodeAgentDbSeeder>();

    var app = builder.Build();

    // Seed database on startup
    using (var scope = app.Services.CreateScope())
    {
        var seeder = scope.ServiceProvider.GetRequiredService<ClaudeCodeAgentDbSeeder>();
        await seeder.SeedAsync();
    }

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
    app.UseStaticFiles();

    var a2AServerOptions = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<A2AServerOptions>>()
        .Value;
    app.MapAgwA2A(a2AServerOptions.Prefix);
    app.MapControllers();

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
