using Agw.Agents.Execution.Agents.Skills;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Runtime;

using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Agw.Setup.Tests;

public class DbSeederTests
{
    private static readonly Guid GeneralAgentId = Guid.Parse("11111111-1111-1111-6666-000000000001");
    private static readonly Guid LocationExtractorAgentId = Guid.Parse("11111111-1111-1111-6666-000000000002");
    private static readonly Guid AmapPoiSearchAgentId = Guid.Parse("11111111-1111-1111-6666-000000000003");
    private static readonly Guid AgentflowId = Guid.Parse("11111111-1111-1111-7777-000000000001");
    private static readonly Guid ModelProviderId = Guid.Parse("11111111-1111-1111-5555-000000000001");
    private static readonly Guid SkillId = Guid.Parse("11111111-1111-1111-8888-000000000001");
    private static readonly Guid JobManagementSkillId = Guid.Parse("11111111-1111-1111-8888-000000000002");

    [Fact]
    public async Task SeedAsync_NewDatabase_CreatesDefaultDefinitionsAndRemainsIdempotent()
    {
        var paths = CreatePaths();
        try
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={Path.Combine(paths.Root, "seed.db")}")
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var context = new AgwDbContext(options);
            var seeder = new DbSeeder(
                context,
                NullLogger<DbSeeder>.Instance,
                TimeProvider.System,
                paths,
                [new TestSkillRegistration()]);

            await seeder.SeedAsync();
            await seeder.SeedAsync();

            var projects = await context.Projects.ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, projects.Count);
            Assert.DoesNotContain(projects, project => project.Name == "claude-code");
            Assert.DoesNotContain(projects, project => project.Name == "codex");
            Assert.Equal(5, await context.Agents.CountAsync(TestContext.Current.CancellationToken));

            var model = await context.Models
                .Include(x => x.Providers)
                .SingleAsync(x => x.Name == "deepseek-v4-pro", TestContext.Current.CancellationToken);
            Assert.Equal(256_000, model.MaxTokens);
            Assert.Equal(2, model.Providers.Count);
            Assert.All(model.Providers, relation => Assert.Equal(60, relation.RpsLimit));

            var providerTypes = await context.Providers
                .OrderBy(x => x.ProviderType)
                .Select(x => x.ProviderType)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal([ProviderType.OpenAIChatCompletions, ProviderType.Anthropic], providerTypes);
            Assert.Empty(await context.ProviderAuthConfigs.ToListAsync(TestContext.Current.CancellationToken));

            var agents = await context.Agents
                .Where(x => x.Id == AmapPoiSearchAgentId || x.Name == "general-agent" || x.Name == "location-extractor")
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(3, agents.Count);
            Assert.Equal(GeneralAgentId, agents.Single(x => x.Name == "general-agent").Id);
            Assert.Equal(LocationExtractorAgentId, agents.Single(x => x.Name == "location-extractor").Id);
            Assert.Equal(AmapPoiSearchAgentId, agents.Single(x => x.Name == "amap-poi-search").Id);
            Assert.All(agents, agent => Assert.Equal(ModelProviderId, agent.ModelProviderId));

            var skill = await context.Skills
                .SingleAsync(x => x.Name == "xhs-explore", TestContext.Current.CancellationToken);
            Assert.Equal(SkillId, skill.Id);
            Assert.Equal("skills/xhs-explore", skill.ContentPath);
            Assert.True(await context.AgentSkillRelations.AnyAsync(
                x => x.AgentId == AmapPoiSearchAgentId && x.SkillId == skill.Id,
                TestContext.Current.CancellationToken));

            var builtInSkill = await context.Skills.SingleAsync(
                x => x.Id == JobManagementSkillId,
                TestContext.Current.CancellationToken);
            Assert.Equal("job-management", builtInSkill.Name);
            Assert.Equal(string.Empty, builtInSkill.ContentPath);
            Assert.Equal(
                1,
                await context.Skills.CountAsync(
                    x => x.Id == JobManagementSkillId,
                    TestContext.Current.CancellationToken));

            var skillMarkdown = Path.Combine(paths.SkillsDirectory, "xhs-explore", "SKILL.md");
            Assert.True(File.Exists(skillMarkdown));
            Assert.Contains(
                "name: xhs-explore",
                await File.ReadAllTextAsync(skillMarkdown, TestContext.Current.CancellationToken));

            var agentflow = await context.Agentflows
                .Include(x => x.Nodes)
                .Include(x => x.Edges)
                .SingleAsync(x => x.Id == AgentflowId, TestContext.Current.CancellationToken);
            Assert.Equal("Xiaohongshu Address Extraction", agentflow.Name);
            Assert.Equal(4, agentflow.Nodes.Count);
            Assert.Equal(3, agentflow.Edges.Count);
            Assert.Contains(agentflow.Nodes, node => node.Kind == AgentflowNodeKind.Input);
            Assert.Contains(agentflow.Nodes, node => node.RelateId == GeneralAgentId);
            Assert.Contains(agentflow.Nodes, node => node.RelateId == LocationExtractorAgentId);
            Assert.Contains(agentflow.Nodes, node => node.RelateId == AmapPoiSearchAgentId);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task SeedAsync_ClassSkillNameOwnedByUser_PreservesUserSkillAndSkipsBuiltIn()
    {
        var paths = CreatePaths();
        try
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={Path.Combine(paths.Root, "conflict.db")}")
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var userSkillId = Guid.CreateVersion7();
            context.Skills.Add(new Skill
            {
                Id = userSkillId,
                Name = "job-management",
                Description = "User-owned skill",
                ContentPath = "skills/job-management",
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            var seeder = new DbSeeder(
                context,
                NullLogger<DbSeeder>.Instance,
                TimeProvider.System,
                paths,
                [new TestSkillRegistration()]);

            await seeder.SeedAsync();

            var skill = await context.Skills.SingleAsync(
                x => x.Name == "job-management",
                TestContext.Current.CancellationToken);
            Assert.Equal(userSkillId, skill.Id);
            Assert.Equal("User-owned skill", skill.Description);
            Assert.False(await context.Skills.AnyAsync(
                x => x.Id == JobManagementSkillId,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    private static AgwDataPaths CreatePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agw-seeder-{Guid.CreateVersion7():N}");
        var paths = AgwDataPaths.Resolve(root, "/unused");
        paths.EnsureCreated();
        return paths;
    }

    private sealed class TestSkillRegistration : IAgentSkillRegistration
    {
        public Guid Id => JobManagementSkillId;

        public string Name => "job-management";

        public string Description => "Manage jobs.";

        public AgentSkill Create(Guid projectId) => throw new NotSupportedException();
    }
}
