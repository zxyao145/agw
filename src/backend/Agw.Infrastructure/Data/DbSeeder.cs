using Agw.Agents.Domain.Entities;
using Agw.Agents.ExternalAgents;
using Agw.Providers.Domain.Entities;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Enums;
using Agw.Shared.Utils;

using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agw.Infrastructure.Data;

/// <summary>
/// Database seeder for initializing default data on application startup.
/// </summary>
public class DbSeeder
{
    public static IReadOnlyList<Project> BuiltInProjects { get; } =
    [
        new Project
        {
            Id = ProjectDefaults.DefaultBuiltInId,
            Name = ProjectDefaults.DefaultBuiltInName,
            Description = "Default built-in project for general task execution.",
            Type = ProjectType.DefaultBuiltIn,
            Enable = true
        },
        new Project
        {
            Id = ProjectDefaults.ClaudeCodeId,
            Name = ProjectDefaults.ClaudeCodeName,
            Description = "Built-in project for Claude Code task execution.",
            Type = ProjectType.DefaultBuiltIn,
            Enable = true,
            ExtraSetting = JsonUtil.Serialize(
                new ClaudeCodeAIAgentOptions()
                    {
                        PermissionMode = PermissionMode.bypassPermissions,
                    }
                ),
        },
        new Project
        {
            Id = ProjectDefaults.A2AId,
            Name = ProjectDefaults.A2AName,
            Description = "Built-in project for A2A task execution.",
            Type = ProjectType.DefaultBuiltIn,
            Enable = true
        }
    ];

    private readonly AgwDbContext _context;
    private readonly ILogger<DbSeeder> _logger;

    public DbSeeder(AgwDbContext context, ILogger<DbSeeder> logger)
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
            await SeedExternalAgentsAsync();
            await SeedProvidersAsync();

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
        foreach (var definition in BuiltInProjects)
        {
            var existingProject = await _context.Projects
                .FirstOrDefaultAsync(project => project.Id == definition.Id || project.Name == definition.Name);

            if (existingProject == null)
            {
                _logger.LogInformation("Seeding built-in project {ProjectName}", definition.Name);
                _context.Projects.Add(CreateBuiltInProject(definition));
                continue;
            }

            //existingProject.Name = definition.Name;
            //existingProject.Type = definition.Type;
            //existingProject.Description = definition.Description;
            //existingProject.Enable = true;
            //existingProject.UpdateTime = DateTime.UtcNow;
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

    private async Task SeedExternalAgentsAsync()
    {
        foreach (var agent in AgentNames.ExternalAgentNames)
        {
            var agentName = agent.Name;
            var existingAgent = await _context.Agents
                .FirstOrDefaultAsync(a => a.Name == agentName && a.Type == AgentType.External);

            if (existingAgent != null)
            {
                _logger.LogInformation("Claude Code Agent already exists, skipping seed");
                return;
            }

            _logger.LogInformation("Seeding External Agent: {agentName}", agentName);
            var agentDefinition = CreateBuiltInAgent(agent);
            _context.Agents.Add(agentDefinition);
        }
    }

    private static Agent CreateBuiltInAgent(Agent definition)
    {
        var now = DateTime.UtcNow;
        return new Agent
        {
            Id = definition.Id,
            DisplayName = definition.DisplayName,
            Name = definition.Name,
            Type = definition.Type,
            Description = definition.Description,
            Extra = definition.Extra,

            CreateBy = "system",
            CreateTime = now,
            UpdateBy = "system",
            UpdateTime = now
        };
    }

    private async Task SeedProvidersAsync()
    {
        List<Provider> providers = new List<Provider>()
        {
            new Provider
            {
                Id = Guid.CreateVersion7(),
                Name = "DeepSeek",
                ProviderType = ProviderType.OpenAI,
                Endpoint = "https://api.deepseek.com",
                Description = "DeepSeek OpenAI Compatible",

                CreateBy = "system",
                CreateTime = DateTime.UtcNow,
                UpdateBy = "system",
                UpdateTime = DateTime.UtcNow
            },
            new Provider
            {
                Id = Guid.CreateVersion7(),
                Name = "DeepSeek",
                ProviderType = ProviderType.Anthropic,
                Endpoint = "https://api.deepseek.com/anthropic",
                Description = "DeepSeek Anthropic Compatible",

                CreateBy = "system",
                CreateTime = DateTime.UtcNow,
                UpdateBy = "system",
                UpdateTime = DateTime.UtcNow
            },
            new Provider
            {
                Id = Guid.CreateVersion7(),
                Name = "Z AI",
                ProviderType = ProviderType.OpenAI,
                Endpoint = "https://open.bigmodel.cn/api/paas/v4",
                Description = "Z AI OpenAI Compatible",

                CreateBy = "system",
                CreateTime = DateTime.UtcNow,
                UpdateBy = "system",
                UpdateTime = DateTime.UtcNow
            },
            new Provider
            {
                Id = Guid.CreateVersion7(),
                Name = "Z AI",
                ProviderType = ProviderType.Anthropic,
                Endpoint = "https://open.bigmodel.cn/api/anthropic",
                Description = "Z AI Anthropic Compatible",

                CreateBy = "system",
                CreateTime = DateTime.UtcNow,
                UpdateBy = "system",
                UpdateTime = DateTime.UtcNow
            }
        };

        foreach (var provider in providers)
        {
            var existingProvider = await _context.Providers
                .FirstOrDefaultAsync(p => p.Name == provider.Name && p.ProviderType == provider.ProviderType);
            if (existingProvider != null)
            {
                _logger.LogInformation("Provider {ProviderName} of type {ProviderType} already exists, skipping seed",
                    provider.Name, provider.ProviderType);
                continue;
            }
            _logger.LogInformation("Seeding Provider: {ProviderName} of type {ProviderType}",
                provider.Name, provider.ProviderType);
            _context.Providers.Add(provider);
        }
    }
}
