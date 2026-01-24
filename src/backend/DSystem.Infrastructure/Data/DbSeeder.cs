using ClaudeCodeSdk.MAF;
using DSystem.Domain.Entities;
using DSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DSystem.Infrastructure.Data;

/// <summary>
/// Database seeder for initializing default data on application startup.
/// </summary>
public class DbSeeder
{
    private readonly LlmDbContext _context;
    private readonly ILogger<DbSeeder> _logger;

    public DbSeeder(LlmDbContext context, ILogger<DbSeeder> logger)
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
        const string ClaudeCodeAgentName = "Claude Code Agent";

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

        // Create a dummy ModelProviderApiKey since Agent requires it
        // In a real scenario, this should be properly configured
        var dummyApiKeyId = Guid.NewGuid();

        // Check if we need to create a dummy ModelProvider and ModelProviderApiKey
        // For now, we'll create a minimal agent with a placeholder API key
        var dummyModelProviderId = Guid.NewGuid();

        // Create dummy ModelProviderApiKey if needed
        var existingApiKey = await _context.ModelProviderApiKeys.FirstOrDefaultAsync();
        if (existingApiKey == null)
        {
            _logger.LogWarning("No ModelProviderApiKey found. Claude Code Agent requires a valid ModelProviderApiKey. Please configure one manually.");
            // We'll skip creating the agent if no API key exists
            return;
        }

        // Use the first available API key
        dummyApiKeyId = existingApiKey.Id;

        var claudeCodeAgent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = ClaudeCodeAgentName,
            Description = "External agent for Claude Code integration with AI-powered coding assistance",
            SystemPrompt = "You are a helpful AI coding assistant powered by Claude Code.",
            Type = AgentType.External,
            Extra = extraJson,
            ModelProviderApiKeyId = dummyApiKeyId,
            Tools = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.Agents.Add(claudeCodeAgent);
        _logger.LogInformation("Claude Code Agent seeded successfully with ID: {AgentId}", claudeCodeAgent.Id);
    }
}
