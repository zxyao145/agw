using DSystem.Api.Controllers;
using DSystem.Domain.Services;
using DSystem.Host;
using DSystem.Infrastructure;
using DSystem.Manager.Api.Controllers;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

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

app.UseHttpsRedirection();
app.MapControllers();

app.Run();
