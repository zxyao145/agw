using DSystem.ExternalAgents;
using DSystem.Infrastructure;
using Microsoft.Extensions.Caching.Hybrid;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHybridCache();
builder.Services.AddScoped<ClaudeCodeService>();
builder.Services.AddScoped<IGitCommandService, GitCommandService>();

var app = builder.Build();

app.UseWebSockets();
app.MapControllers();

app.Run();
