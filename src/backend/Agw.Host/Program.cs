using Agw.Api.Controllers;
using Agw.Infrastructure;
using Agw.Infrastructure.Data;
using Agw.Jobs;
using Agw.Manager.Api.Controllers;
using Agw.Providers;
using Agw.Skills;
using Agw.Tasks;
using Agw.Tools;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Enrichers.OpenTelemetry;

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
        .AddApplicationPart(typeof(SkillsController).Assembly)
        .AddApplicationPart(typeof(JobsController).Assembly);
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddOpenApi();
    builder.Services.AddHttpClient();

    // add module
    builder.Services
        // TODO
        //.AddA2A(builder.Configuration)
        .AddAgents(builder.Configuration)
        .AddInfrastructure(builder.Configuration)
        .AddJobs(builder.Configuration)
        .AddProviders(builder.Configuration)
        .AddSkills(builder.Configuration)
        .AddTasks(builder.Configuration)
        .AddTools(builder.Configuration)
        ;

    builder.Services.AddHybridCache();

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

    // TODO: A2A
    //var a2AServerOptions = app.Services
    //    .GetRequiredService<Microsoft.Extensions.Options.IOptions<AgwA2AServerOptions>>()
    //    .Value;
    //app.MapAgwA2A(a2AServerOptions.Prefix);
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
