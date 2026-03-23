using Agw.Domain.Entities;
using Agw.Shared.Enums;
using Agw.Shared.Tasks;
using Agw.Shared.Tasks.Entities;
using Agw.Shared.Utils;
using ClaudeCodeSdk.MAF;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agw.Infrastructure.Data;

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

            await SeedBuiltInProjectsAsync();
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

    private async Task SeedBuiltInProjectsAsync()
    {
        foreach (var definition in ProjectDefaults.BuiltInProjects)
        {
            var existingProject = await _context.Projects
                .FirstOrDefaultAsync(project => project.Id == definition.Id || project.Name == definition.Name);

            if (existingProject == null)
            {
                _logger.LogInformation("Seeding built-in project {ProjectName}", definition.Name);
                _context.Projects.Add(CreateBuiltInProject(definition));
                continue;
            }

            existingProject.Name = definition.Name;
            existingProject.Type = definition.Type;
            existingProject.Description = definition.Description;
            existingProject.Enable = true;
            existingProject.UpdateTime = DateTime.UtcNow;
        }
    }

    private static Project CreateBuiltInProject(Project definition)
    {
        var now = DateTime.UtcNow;
        return new Project
        {
            Id = definition.Id,
            Name = definition.Name,
            Type = definition.Type,
            Description = definition.Description,
            Workspace = definition.Workspace,
            Enable = true,
            ExtraSetting = definition.ExtraSetting,
            CreateBy = "system",
            CreateTime = now,
            UpdateBy = "system",
            UpdateTime = now
        };
    }

    private async Task SeedClaudeCodeAgentAsync()
    {
        const string ClaudeCodeAgentName = "ClaudeCode";

        var existingAgent = await _context.Agents
            .FirstOrDefaultAsync(a => a.Name == ClaudeCodeAgentName && a.Type == AgentType.External);

        if (existingAgent != null)
        {
            _logger.LogInformation("Claude Code Agent already exists, skipping seed");
            return;
        }

        _logger.LogInformation("Seeding Claude Code Agent");

        var claudeCodeOptions = new ClaudeCodeAIAgentOptions();
        var extraJson = JsonUtil.Serialize(claudeCodeOptions);

        var claudeCodeAgent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = ClaudeCodeAgentName,
            Description = "External agent for Claude Code integration with AI-powered coding assistance",
            SystemPrompt = string.Empty,
            Type = AgentType.External,
            Extra = extraJson,
            ModelProviderId = null,
            Tools = null,
            CreateTime = DateTime.UtcNow,
            UpdateTime = DateTime.UtcNow
        };

        _context.Agents.Add(claudeCodeAgent);
        _logger.LogInformation("Claude Code Agent seeded successfully with ID: {AgentId}", claudeCodeAgent.Id);
    }
}
