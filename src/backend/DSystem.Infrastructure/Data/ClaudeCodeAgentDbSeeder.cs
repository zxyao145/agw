using ClaudeCodeSdk.MAF;
using DSystem.Domain.Entities;
using DSystem.Shared;
using DSystem.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DSystem.Infrastructure.Data;

/// <summary>
/// Database seeder for initializing default data on application startup.
/// </summary>
public class ClaudeCodeAgentDbSeeder
{
    private readonly LlmDbContext _context;
    private readonly ILogger<ClaudeCodeAgentDbSeeder> _logger;

    public ClaudeCodeAgentDbSeeder(LlmDbContext context, ILogger<ClaudeCodeAgentDbSeeder> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Seeds the database with default data.
    /// </summary>
    public async Task SeedAsync()
    {
        try
        {
            _logger.LogInformation("Starting database seeding");

            // Ensure database is created
            await _context.Database.EnsureCreatedAsync();

            // Seed Claude Code agent if it doesn't exist
            await SeedClaudeCodeAgentAsync();

            await _context.SaveChangesAsync();
            _logger.LogInformation("Database seeding completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during database seeding");
            throw;
        }
    }

    private async Task SeedClaudeCodeAgentAsync()
    {
        const string ClaudeCodeAgentName = "ClaudeCode";

        // Check if Claude Code agent already exists
        var existingAgent = await _context.Agents
            .FirstOrDefaultAsync(a => a.Name == ClaudeCodeAgentName && a.Type == AgentType.External);

        if (existingAgent != null)
        {
            _logger.LogInformation("Claude Code Agent already exists, skipping seed");
            return;
        }

        _logger.LogInformation("Seeding Claude Code Agent");

        // Create default ClaudeCodeAIAgentOptions
        var claudeCodeOptions = new ClaudeCodeAIAgentOptions();
        var extraJson = JsonUtil.Serialize(claudeCodeOptions);

        // External agents don't require a ModelProviderApiKey
        var claudeCodeAgent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = ClaudeCodeAgentName,
            Description = "External agent for Claude Code integration with AI-powered coding assistance",
            SystemPrompt = string.Empty,
            Type = AgentType.External,
            Extra = extraJson,
            ModelProviderApiKeyId = null,  // External agents can have null ModelProviderApiKeyId
            Tools = null,
            CreateTime = DateTime.UtcNow,
            UpdateTime = DateTime.UtcNow
        };

        _context.Agents.Add(claudeCodeAgent);
        _logger.LogInformation("Claude Code Agent seeded successfully with ID: {AgentId}", claudeCodeAgent.Id);
    }
}
