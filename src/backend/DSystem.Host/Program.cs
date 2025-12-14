using DSystem.Domain.Services;
using DSystem.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.MapControllers();

app.Run();
