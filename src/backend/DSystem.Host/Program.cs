using DSystem.Api.Controllers;
using DSystem.Domain.Services;
using DSystem.Host;
using DSystem.Infrastructure;
using DSystem.Manager.Api.Controllers;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

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
    .AddApplicationPart(typeof(AgentsController).Assembly)
    .AddApplicationPart(typeof(ProjectsController).Assembly);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<ModelDomainService>();
builder.Services.AddScoped<ProviderDomainService>();
builder.Services.AddScoped<ModelProviderDomainService>();
builder.Services.AddScoped<ModelProviderApiKeyDomainService>();
builder.Services.AddScoped<AgentDomainService>();
builder.Services.AddScoped<AgentRuntimeService>();

builder.Services.AddScoped<WorkflowDomainService>();
builder.Services.AddScoped<WorkflowRuntimeService>();
builder.Services.AddScoped<IWorkflowAgentExecutor, PlaceholderWorkflowAgentExecutor>();

builder.Services.AddScoped<ProjectDomainService>();
builder.Services.AddScoped<ProjectTaskDomainService>();
builder.Services.AddHostedService<ProjectTaskSchedulerHostedService>();

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

app.MapControllers();

app.Run();
