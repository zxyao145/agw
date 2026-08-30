using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Shared;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Runtime;
using Agw.Shared.Tooling;
using Agw.Skills.Contracts.Registration;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agw.Setup.Tests;

public class DbSeederTests : IDisposable
{
    private static readonly Guid GeneralAgentId = Guid.Parse("11111111-1111-1111-6666-000000000001");
    private static readonly Guid LocationExtractorAgentId = Guid.Parse("11111111-1111-1111-6666-000000000002");
    private static readonly Guid AmapPoiSearchAgentId = Guid.Parse("11111111-1111-1111-6666-000000000003");
    private static readonly Guid AgentflowId = Guid.Parse("11111111-1111-1111-7777-000000000001");
    private static readonly Guid ModelProviderId = Guid.Parse("11111111-1111-1111-5555-000000000001");
    private static readonly Guid SkillId = Guid.Parse("11111111-1111-1111-8888-000000000001");
    private static readonly Guid JobManagementSkillId = Guid.Parse("11111111-1111-1111-8888-000000000002");
    private readonly IDisposable _systemScope = UserInfoUtil.PushSystemScope();

    public void Dispose() => _systemScope.Dispose();

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
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var seeder = new DbSeeder(
                context,
                NullLogger<DbSeeder>.Instance,
                TimeProvider.System,
                paths,
                [new TestSkillRegistration()]
            );

            await seeder.SeedAsync();
            await seeder.SeedAsync();

            var projects = await context.Projects.ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, projects.Count);
            Assert.All(projects, project => Assert.Equal(Constants.AdminUserId, project.CreateBy));
            Assert.All(projects, project => Assert.Equal(Constants.AdminUserId, project.UpdateBy));
            Assert.DoesNotContain(projects, project => project.Name == "claude-code");
            Assert.DoesNotContain(projects, project => project.Name == "codex");
            Assert.Equal(5, await context.Agents.CountAsync(TestContext.Current.CancellationToken));

            var model = await context
                .Models.Include(x => x.Providers)
                .SingleAsync(x => x.Name == "deepseek-v4-pro", TestContext.Current.CancellationToken);
            Assert.Equal(AgwAiModel.DefaultMaxContextWindowTokens, model.MaxContextWindowTokens);
            Assert.Equal(AgwAiModel.DefaultMaxOutputTokens, model.MaxOutputTokens);
            Assert.Equal(Constants.AdminUserId, model.CreateBy);
            Assert.Equal(Constants.AdminUserId, model.UpdateBy);
            Assert.Equal(2, model.Providers.Count);
            Assert.All(model.Providers, relation => Assert.Equal(60, relation.RpsLimit));
            Assert.All(model.Providers, relation => Assert.Equal(Constants.AdminUserId, relation.CreateBy));
            Assert.All(model.Providers, relation => Assert.Equal(Constants.AdminUserId, relation.UpdateBy));

            var providerTypes = await context
                .Providers.OrderBy(x => x.ProviderType)
                .Select(x => x.ProviderType)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal([ProviderType.OpenAIChatCompletions, ProviderType.Anthropic], providerTypes);
            Assert.Empty(await context.ProviderAuthConfigs.ToListAsync(TestContext.Current.CancellationToken));

            var agents = await context
                .Agents.Where(x =>
                    x.Id == AmapPoiSearchAgentId || x.Name == "general-agent" || x.Name == "location-extractor"
                )
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(3, agents.Count);
            Assert.All(agents, agent => Assert.Equal(Constants.AdminUserId, agent.CreateBy));
            Assert.All(agents, agent => Assert.Equal(Constants.AdminUserId, agent.UpdateBy));
            Assert.Equal(GeneralAgentId, agents.Single(x => x.Name == "general-agent").Id);
            Assert.Equal(LocationExtractorAgentId, agents.Single(x => x.Name == "location-extractor").Id);
            Assert.Equal(AmapPoiSearchAgentId, agents.Single(x => x.Name == "amap-poi-search").Id);
            Assert.All(agents, agent => Assert.Equal(ModelProviderId, agent.ModelProviderId));
            Assert.Collection(
                agents.Single(x => x.Name == "general-agent").Tools,
                value => Assert.IsType<DiffToolDefinition>(Assert.IsType<ToolValue>(value).Definition),
                value => Assert.IsType<GitCloneToolDefinition>(Assert.IsType<ToolValue>(value).Definition),
                value => Assert.IsType<RunShellToolDefinition>(Assert.IsType<ToolValue>(value).Definition),
                value => Assert.IsType<FileAccessToolBlockDefinition>(Assert.IsType<ToolBlockValue>(value).Definition)
            );
            Assert.Collection(
                agents.Single(x => x.Name == "location-extractor").Tools,
                value => Assert.IsType<WebFetchToolDefinition>(Assert.IsType<ToolValue>(value).Definition),
                value => Assert.IsType<WebSearchToolDefinition>(Assert.IsType<ToolValue>(value).Definition),
                value => Assert.IsType<TodoToolBlockDefinition>(Assert.IsType<ToolBlockValue>(value).Definition)
            );

            var skill = await context.Skills.SingleAsync(
                x => x.Name == "xhs-explore",
                TestContext.Current.CancellationToken
            );
            Assert.Equal(SkillId, skill.Id);
            Assert.Equal(SkillKind.Local, skill.Kind);
            Assert.Equal(Constants.AdminUserId, skill.CreateBy);
            Assert.Equal(Constants.AdminUserId, skill.UpdateBy);
            Assert.Equal("skills/xhs-explore", skill.ContentPath);
            Assert.Null(skill.RemoteUrl);
            Assert.True(
                await context.AgentSkillRelations.AnyAsync(
                    x => x.AgentId == AmapPoiSearchAgentId && x.SkillId == skill.Id,
                    TestContext.Current.CancellationToken
                )
            );

            var builtInSkill = await context.Skills.SingleAsync(
                x => x.Id == JobManagementSkillId,
                TestContext.Current.CancellationToken
            );
            Assert.Equal("agw-job", builtInSkill.Name);
            Assert.Equal(SkillKind.BuiltIn, builtInSkill.Kind);
            Assert.Equal(Constants.AdminUserId, builtInSkill.CreateBy);
            Assert.Equal(Constants.AdminUserId, builtInSkill.UpdateBy);
            Assert.Equal(string.Empty, builtInSkill.ContentPath);
            Assert.Null(builtInSkill.RemoteUrl);
            Assert.Equal(
                1,
                await context.Skills.CountAsync(
                    x => x.Id == JobManagementSkillId,
                    TestContext.Current.CancellationToken
                )
            );

            var skillMarkdown = Path.Combine(paths.SkillsDirectory, "xhs-explore", "SKILL.md");
            Assert.True(File.Exists(skillMarkdown));
            var skillMarkdownContent = await File.ReadAllTextAsync(
                skillMarkdown,
                TestContext.Current.CancellationToken
            );
            Assert.Contains("name: xhs-explore", skillMarkdownContent, StringComparison.Ordinal);
            Assert.Contains("`run_skill_script`", skillMarkdownContent, StringComparison.Ordinal);
            Assert.Contains("\"scriptName\": \"scripts/cli.py\"", skillMarkdownContent, StringComparison.Ordinal);
            Assert.DoesNotContain("python scripts/cli.py", skillMarkdownContent, StringComparison.Ordinal);
            var skillCli = Path.Combine(paths.SkillsDirectory, "xhs-explore", "scripts", "cli.py");
            var skillCliContent = await File.ReadAllTextAsync(skillCli, TestContext.Current.CancellationToken);
            Assert.Contains("\"stdin\": subprocess.DEVNULL", skillCliContent, StringComparison.Ordinal);
            Assert.Contains("\"stdout\": subprocess.DEVNULL", skillCliContent, StringComparison.Ordinal);
            Assert.Contains("\"stderr\": subprocess.DEVNULL", skillCliContent, StringComparison.Ordinal);

            var agentflow = await context
                .Agentflows.Include(x => x.Nodes)
                .Include(x => x.Edges)
                .SingleAsync(x => x.Id == AgentflowId, TestContext.Current.CancellationToken);
            Assert.Equal("Xiaohongshu Address Extraction", agentflow.Name);
            Assert.Equal(Constants.AdminUserId, agentflow.CreateBy);
            Assert.Equal(Constants.AdminUserId, agentflow.UpdateBy);
            Assert.Equal(4, agentflow.Nodes.Count);
            Assert.Equal(3, agentflow.Edges.Count);
            Assert.All(agentflow.Nodes, node => Assert.Equal(Constants.AdminUserId, node.CreateBy));
            Assert.All(agentflow.Nodes, node => Assert.Equal(Constants.AdminUserId, node.UpdateBy));
            Assert.All(agentflow.Edges, edge => Assert.Equal(Constants.AdminUserId, edge.CreateBy));
            Assert.All(agentflow.Edges, edge => Assert.Equal(Constants.AdminUserId, edge.UpdateBy));
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
    public async Task SeedAsync_LegacyJobManagementSkill_RenamesFixedBuiltInSkill()
    {
        var paths = CreatePaths();
        try
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={Path.Combine(paths.Root, "legacy-skill.db")}")
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            context.Skills.Add(
                new Skill
                {
                    Id = JobManagementSkillId,
                    Name = "job-management",
                    Description = "Legacy job skill",
                    Kind = SkillKind.BuiltIn,
                    ContentPath = string.Empty,
                    CreateBy = Constants.AdminUserId,
                }
            );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            var seeder = new DbSeeder(
                context,
                NullLogger<DbSeeder>.Instance,
                TimeProvider.System,
                paths,
                [new TestSkillRegistration()]
            );

            await seeder.SeedAsync();

            var skill = await context.Skills.SingleAsync(
                x => x.Id == JobManagementSkillId,
                TestContext.Current.CancellationToken
            );
            Assert.Equal("agw-job", skill.Name);
            Assert.Equal("Manage jobs.", skill.Description);
            Assert.Equal(SkillKind.BuiltIn, skill.Kind);
            Assert.False(
                await context.Skills.AnyAsync(x => x.Name == "job-management", TestContext.Current.CancellationToken)
            );
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task SeedAsync_ClassSkillNameOwnedByUser_PreservesUserSkillAndSeedsGlobalBuiltIn()
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
            context.Skills.Add(
                new Skill
                {
                    Id = userSkillId,
                    Name = "agw-job",
                    Description = "User-owned skill",
                    Kind = SkillKind.Local,
                    ContentPath = "skills/agw-job",
                    CreateBy = "user-a",
                }
            );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            var seeder = new DbSeeder(
                context,
                NullLogger<DbSeeder>.Instance,
                TimeProvider.System,
                paths,
                [new TestSkillRegistration()]
            );

            await seeder.SeedAsync();

            var skills = await context
                .Skills.Where(x => x.Name == "agw-job")
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, skills.Count);
            var userSkill = Assert.Single(skills, skill => skill.Id == userSkillId);
            Assert.Equal("User-owned skill", userSkill.Description);
            Assert.Equal(SkillKind.Local, userSkill.Kind);
            Assert.Equal("user-a", userSkill.CreateBy);
            var builtIn = Assert.Single(skills, skill => skill.Id == JobManagementSkillId);
            Assert.Equal(SkillKind.BuiltIn, builtIn.Kind);
            Assert.Equal(Constants.AdminUserId, builtIn.CreateBy);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task SeedAsync_ReservedDefaultSkillOwnedByUser_IsNotMutated()
    {
        var paths = CreatePaths();
        try
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={Path.Combine(paths.Root, "reserved-skill.db")}")
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            context.Skills.Add(
                new Skill
                {
                    Id = SkillId,
                    Name = "user-owned-reserved-id",
                    Description = "Must remain untouched",
                    Kind = SkillKind.Local,
                    ContentPath = "skills/user-owned-reserved-id",
                    CreateBy = "user-a",
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            context.ChangeTracker.Clear();

            var seeder = new DbSeeder(
                context,
                NullLogger<DbSeeder>.Instance,
                TimeProvider.System,
                paths,
                [new TestSkillRegistration()]
            );

            await seeder.SeedAsync();

            using var systemScope = UserInfoUtil.PushSystemScope();
            var skill = await context.Skills.SingleAsync(x => x.Id == SkillId, TestContext.Current.CancellationToken);
            Assert.Equal("user-owned-reserved-id", skill.Name);
            Assert.Equal("user-a", skill.CreateBy);
            Assert.Equal(SkillKind.Local, skill.Kind);
            Assert.False(
                await context.Skills.AnyAsync(
                    x => x.Id == SkillId && x.CreateBy == Constants.AdminUserId,
                    TestContext.Current.CancellationToken
                )
            );
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeedAsync_DefaultToolRegression_BackfillsKnownSignaturesAndPreservesCustomization(
        bool includesFileAccess
    )
    {
        var paths = CreatePaths();
        try
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={Path.Combine(paths.Root, "agent-tools.db")}")
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            context.Agents.AddRange(
                new Agent
                {
                    Id = GeneralAgentId,
                    Name = "general-agent",
                    DisplayName = "General Agent",
                    Type = AgentType.System,
                    Tools = CreateLegacyGeneralAgentTools(includesFileAccess),
                    CreateBy = Constants.AdminUserId,
                },
                new Agent
                {
                    Id = LocationExtractorAgentId,
                    Name = "location-extractor",
                    DisplayName = "Location Extractor",
                    Type = AgentType.System,
                    CreateBy = Constants.AdminUserId,
                    Tools =
                    [
                        new ToolValue { Definition = new WebFetchToolDefinition() },
                        new ToolValue { Definition = new BashToolDefinition() },
                    ],
                }
            );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            var seeder = new DbSeeder(
                context,
                NullLogger<DbSeeder>.Instance,
                TimeProvider.System,
                paths,
                [new TestSkillRegistration()]
            );

            await seeder.SeedAsync();
            await seeder.SeedAsync();

            var general = await context.Agents.SingleAsync(
                agent => agent.Id == GeneralAgentId,
                TestContext.Current.CancellationToken
            );
            Assert.Collection(
                general.Tools,
                value => Assert.IsType<DiffToolDefinition>(Assert.IsType<ToolValue>(value).Definition),
                value => Assert.IsType<GitCloneToolDefinition>(Assert.IsType<ToolValue>(value).Definition),
                value => Assert.IsType<RunShellToolDefinition>(Assert.IsType<ToolValue>(value).Definition),
                value => Assert.IsType<FileAccessToolBlockDefinition>(Assert.IsType<ToolBlockValue>(value).Definition)
            );
            var location = await context.Agents.SingleAsync(
                agent => agent.Id == LocationExtractorAgentId,
                TestContext.Current.CancellationToken
            );
            Assert.Collection(
                location.Tools,
                value => Assert.IsType<WebFetchToolDefinition>(Assert.IsType<ToolValue>(value).Definition),
                value => Assert.IsType<BashToolDefinition>(Assert.IsType<ToolValue>(value).Definition)
            );
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    private static List<ToolValueObject> CreateLegacyGeneralAgentTools(bool includesFileAccess)
    {
        List<ToolValueObject> tools =
        [
            new ToolValue { Definition = new DiffToolDefinition() },
            new ToolValue { Definition = new GitCloneToolDefinition() },
            new ToolValue { Definition = new BashToolDefinition() },
        ];
        if (includesFileAccess)
        {
            tools.Add(new ToolBlockValue { Definition = new FileAccessToolBlockDefinition() });
        }

        return tools;
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

        public string Name => "agw-job";

        public string Description => "Manage jobs.";

        public AgentSkill Create(Guid projectId) => throw new NotSupportedException();
    }
}
