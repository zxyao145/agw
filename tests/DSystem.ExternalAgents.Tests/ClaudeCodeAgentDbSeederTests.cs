using DSystem.Domain.Entities;
using DSystem.Infrastructure.Data;
using DSystem.Shared.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DSystem.ExternalAgents.Tests;

public class ClaudeCodeAgentDbSeederTests
{
    [Fact]
    public async Task SeedAsync_WhenClaudeCodeAgentMissing_AddsExternalAgent()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new LlmDbContext(options);
        var seeder = new ClaudeCodeAgentDbSeeder(context, NullLogger<ClaudeCodeAgentDbSeeder>.Instance);

        await seeder.SeedAsync();

        var agent = await context.Agents.SingleOrDefaultAsync(a => a.Name == "ClaudeCode");

        Assert.NotNull(agent);
        Assert.Equal(AgentType.External, agent!.Type);
        Assert.Null(agent.ModelProviderApiKeyId);
        Assert.False(string.IsNullOrWhiteSpace(agent.Extra));
    }

    [Fact]
    public async Task SeedAsync_WhenClaudeCodeAgentExists_DoesNotCreateDuplicate()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new LlmDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var existing = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "ClaudeCode",
            Type = AgentType.External,
            Description = "Existing",
            SystemPrompt = string.Empty,
            Extra = "{}",
            CreateTime = DateTime.UtcNow,
            UpdateTime = DateTime.UtcNow
        };

        context.Agents.Add(existing);
        await context.SaveChangesAsync();

        var seeder = new ClaudeCodeAgentDbSeeder(context, NullLogger<ClaudeCodeAgentDbSeeder>.Instance);
        await seeder.SeedAsync();

        var count = await context.Agents.CountAsync(a => a.Name == "ClaudeCode" && a.Type == AgentType.External);

        Assert.Equal(1, count);
    }
}
