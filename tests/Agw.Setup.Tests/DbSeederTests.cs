using Agw.Agents.Contracts.Execution;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Files;
using Agw.Files.Abstracts;
using Agw.Infrastructure.Auth;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Settings;
using Agw.Jobs.Application.Skills;
using Agw.Projects;
using Agw.Projects.Contracts;
using Agw.Providers.Contracts;
using Agw.Settings.Application;
using Agw.Shared;
using Agw.Shared.Configuration;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Tooling;
using Agw.Skills.Contracts;
using Agw.Skills.Contracts.Registration;
using Agw.Tests;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
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
    private static readonly Guid PiAgentId = Guid.Parse("11111111-1111-1111-2222-000000000004");
    private readonly IDisposable _systemScope = UserInfoUtil.PushSystemScope();

    public static bool PostgresEnabled => IsolatedTestConnections.Postgres is not null;

    public void Dispose() => _systemScope.Dispose();

    private static IServerAuthStatePersistence CreateAuthentication(AgwDbContext context) =>
        new SettingsServerAuthStatePersistence(
            new SettingsStore(new EfSettingsPersistence(context), new UserInfoService()),
            context,
            TimeProvider.System
        );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintainSystemDataAsync_BuiltInSkillMissingOrOutdated_UpdatesOnlySystemDefinitions(bool exists)
    {
        var paths = CreatePaths();
        try
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={Path.Combine(paths.Root, "maintenance.db")}")
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var registration = new JobManagementSkillRegistration();
            if (exists)
                context.Skills.Add(
                    new Skill
                    {
                        Id = registration.Id,
                        Name = "job-management",
                        Description = "Legacy metadata",
                        Kind = SkillKind.BuiltIn,
                        CreateBy = Constants.AdminUserId,
                    }
                );
            var userSkill = new Skill
            {
                Id = Guid.CreateVersion7(),
                Name = registration.Name,
                Description = "User content",
                Kind = SkillKind.Local,
                ContentPath = "skills/custom",
                CreateBy = "user-a",
            };
            context.Skills.Add(userSkill);
            AddBuiltInAgentReferences(context);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            context.ChangeTracker.Clear();
            var seeder = new DbSeeder(
                context,
                NullLogger<DbSeeder>.Instance,
                TimeProvider.System,
                paths,
                CreateWorkspaceAccess(paths),
                CreateAuthentication(context),
                [registration]
            );

            await seeder.MaintainSystemDataAsync();
            await seeder.MaintainSystemDataAsync();
            context.ChangeTracker.Clear();

            var builtIn = await context.Skills.SingleAsync(
                skill => skill.Id == registration.Id,
                TestContext.Current.CancellationToken
            );
            Assert.Equal(registration.Name, builtIn.Name);
            Assert.Equal(registration.Description, builtIn.Description);
            Assert.Equal(SkillKind.BuiltIn, builtIn.Kind);
            Assert.Equal(
                "User content",
                (
                    await context.Skills.SingleAsync(
                        skill => skill.Id == userSkill.Id,
                        TestContext.Current.CancellationToken
                    )
                ).Description
            );
            Assert.Equal(2, await context.Skills.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(3, await context.Agents.CountAsync(TestContext.Current.CancellationToken));
            Assert.Single(await context.Agentflows.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await context.Projects.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await context.Settings.ToListAsync(TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(paths.SkillsDirectory, "xhs-explore")));
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

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
                CreateWorkspaceAccess(paths),
                CreateAuthentication(context),
                [new TestSkillRegistration()]
            );

            await seeder.SeedAsync();
            await seeder.SeedAsync();
            await seeder.SeedAsync();

            var projects = await context.Projects.ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, projects.Count);
            Assert.All(projects, project => Assert.Equal(Constants.AdminUserId, project.CreateBy));
            Assert.All(projects, project => Assert.Equal(Constants.AdminUserId, project.UpdateBy));
            Assert.DoesNotContain(projects, project => project.Name == "claude-code");
            Assert.DoesNotContain(projects, project => project.Name == "codex");
            Assert.Equal(6, await context.Agents.CountAsync(TestContext.Current.CancellationToken));
            var piAgent = await context.Agents.SingleAsync(
                agent => agent.Id == PiAgentId,
                TestContext.Current.CancellationToken
            );
            Assert.Equal("Pi", piAgent.Name);
            Assert.Equal(AgentType.External, piAgent.Type);
            Assert.Equal(EngineKind.Pi, piAgent.ExternalAgentKind);
            Assert.Equal(Constants.AdminUserId, piAgent.CreateBy);

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
            Assert.Equal([ProviderType.OpenAIResponses, ProviderType.Anthropic], providerTypes);
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
            var mcpServer = await context.McpToolServers.SingleAsync(
                server => server.Name == "amap-maps-streamableHTTP",
                TestContext.Current.CancellationToken
            );
            Assert.Equal("Amap MCP Server", mcpServer.Description);
            Assert.Equal("http", mcpServer.TransportType);
            Assert.Equal("https://mcp.amap.com/mcp?key=<key>", mcpServer.Url);
            Assert.Equal(Constants.AdminUserId, mcpServer.CreateBy);
            Assert.Equal(Constants.AdminUserId, mcpServer.UpdateBy);
            var relation = await context.AgentMcpToolServers.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(AmapPoiSearchAgentId, relation.AgentId);
            Assert.Equal(mcpServer.Id, relation.McpToolServerId);
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

            var agentflow = await context.Agentflows.SingleAsync(
                item => item.Id == AgentflowId,
                TestContext.Current.CancellationToken
            );
            var nodes = await context
                .AgentflowNodes.Where(node => node.AgentflowId == AgentflowId && node.AgentflowRevision == 1)
                .ToArrayAsync(TestContext.Current.CancellationToken);
            var edges = await context
                .AgentflowEdges.Where(edge => edge.AgentflowId == AgentflowId && edge.AgentflowRevision == 1)
                .ToArrayAsync(TestContext.Current.CancellationToken);
            Assert.Equal("Xiaohongshu Address Extraction", agentflow.Name);
            Assert.Equal(1, agentflow.LatestRevision);
            Assert.Equal(1, agentflow.PublishedRevision);
            Assert.Equal(Constants.AdminUserId, agentflow.CreateBy);
            Assert.Equal(Constants.AdminUserId, agentflow.UpdateBy);
            Assert.Equal(4, nodes.Length);
            Assert.Equal(3, edges.Length);
            Assert.Equal(
                1,
                await context.AgentflowRevisions.CountAsync(
                    revision => revision.AgentflowId == AgentflowId,
                    TestContext.Current.CancellationToken
                )
            );
            Assert.All(nodes, node => Assert.Equal(1, node.AgentflowRevision));
            Assert.Equal(
                AgentflowRoutingStrategy.FanOut,
                nodes.Single(node => node.Kind == AgentflowNodeKind.Input).RoutingStrategy
            );
            Assert.All(nodes, node => Assert.Equal(Constants.AdminUserId, node.CreateBy));
            Assert.All(nodes, node => Assert.Equal(Constants.AdminUserId, node.UpdateBy));
            Assert.All(edges, edge => Assert.Equal(Constants.AdminUserId, edge.CreateBy));
            Assert.All(edges, edge => Assert.Equal(Constants.AdminUserId, edge.UpdateBy));
            Assert.Contains(nodes, node => node.Kind == AgentflowNodeKind.Input);
            Assert.Contains(nodes, node => node.RelateId == GeneralAgentId);
            Assert.Contains(nodes, node => node.RelateId == LocationExtractorAgentId);
            Assert.Contains(nodes, node => node.RelateId == AmapPoiSearchAgentId);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public Task MaintainSystemDataAsync_BuiltinMissing_CreatesPublishedRevisionOne() =>
        VerifySqliteAgentflowMaintenanceAsync(migrate: false);

    [Fact]
    public Task MaintainSystemDataAsync_InitializedSqliteAfterMigration_SeedsBuiltinPublishedRevisionOne() =>
        VerifySqliteAgentflowMaintenanceAsync(migrate: true);

    [Fact(SkipUnless = nameof(PostgresEnabled), Skip = "Requires an isolated PostgreSQL test server.")]
    public async Task MaintainSystemDataAsync_InitializedPostgresAfterMigration_SeedsBuiltinPublishedRevisionOne()
    {
        var admin = new NpgsqlConnectionStringBuilder(IsolatedTestConnections.Postgres) { Pooling = false };
        var database = $"agw_builtin_agentflow_test_{Guid.NewGuid():N}";
        var target = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = database };
        var paths = CreatePaths();
        try
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>();
            AgwDbContextOptionsConfigurator.Configure(options, DatabaseProvider.Postgres, target.ConnectionString);
            await using var context = new AgwDbContext(options.Options);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await VerifyAgentflowMaintenanceAsync(context, paths);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)",
                connection
            );
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static async Task VerifySqliteAgentflowMaintenanceAsync(bool migrate)
    {
        var paths = CreatePaths();
        try
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>();
            var connectionString = $"Data Source={Path.Combine(paths.Root, "builtin-maintenance.db")}";
            AgwDbContextOptionsConfigurator.Configure(options, DatabaseProvider.Sqlite, connectionString);
            await using var context = new AgwDbContext(options.Options);
            var token = TestContext.Current.CancellationToken;
            if (migrate)
                await context.Database.MigrateAsync(token);
            else
                await context.Database.EnsureCreatedAsync(token);
            await VerifyAgentflowMaintenanceAsync(context, paths);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    private static async Task VerifyAgentflowMaintenanceAsync(AgwDbContext context, AgwDataPaths paths)
    {
        var token = TestContext.Current.CancellationToken;
        var seeder = new DbSeeder(
            context,
            NullLogger<DbSeeder>.Instance,
            TimeProvider.System,
            paths,
            CreateWorkspaceAccess(paths),
            CreateAuthentication(context),
            [new TestSkillRegistration()]
        );
        await seeder.SeedAsync();
        var job = NewAgentflowSubscription(EventManagedBy.Job);
        var subscription = NewAgentflowSubscription(EventManagedBy.User);
        context.EventSubscriptions.AddRange(job, subscription);
        await context.SaveChangesAsync(token);
        await DeleteBuiltInAgentflowAsync(context, token);

        await seeder.MaintainSystemDataAsync();

        await AssertBuiltInAgentflowAsync(context, token);
        var references = await context
            .EventSubscriptions.Where(item => item.Id == job.Id || item.Id == subscription.Id)
            .Select(item => item.AgentId)
            .ToArrayAsync(token);
        Assert.Equal(2, references.Length);
        Assert.All(references, reference => Assert.Equal(AgentflowId, reference));
    }

    private static EventSubscription NewAgentflowSubscription(string managedBy) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = $"Agentflow {managedBy} target",
            Enabled = true,
            SourceIdsJson = "[]",
            EventTypesJson = "[]",
            TargetKind = EventSubscriptionTargetKind.Agentflow,
            AgentId = AgentflowId,
            ManagedBy = managedBy,
            ManagedKey = managedBy == EventManagedBy.Job ? Guid.CreateVersion7().ToString("D") : null,
            CreateBy = Constants.AdminUserId,
        };

    private static async Task DeleteBuiltInAgentflowAsync(AgwDbContext context, CancellationToken token)
    {
        await context.AgentflowEdges.Where(edge => edge.AgentflowId == AgentflowId).ExecuteDeleteAsync(token);
        await context.AgentflowNodes.Where(node => node.AgentflowId == AgentflowId).ExecuteDeleteAsync(token);
        await context
            .AgentflowBlockMembers.Where(member => member.AgentflowId == AgentflowId)
            .ExecuteDeleteAsync(token);
        await context
            .AgentflowRevisions.Where(revision => revision.AgentflowId == AgentflowId)
            .ExecuteDeleteAsync(token);
        await context.Agentflows.Where(flow => flow.Id == AgentflowId).ExecuteDeleteAsync(token);
        context.ChangeTracker.Clear();
    }

    private static async Task AssertBuiltInAgentflowAsync(AgwDbContext context, CancellationToken token)
    {
        var restored = await context.Agentflows.SingleAsync(flow => flow.Id == AgentflowId, token);
        Assert.Equal(1, restored.PublishedRevision);
        Assert.Equal(1, restored.LatestRevision);
        Assert.Equal(Constants.AdminUserId, restored.CreateBy);
        Assert.Equal(
            1,
            await context.AgentflowRevisions.CountAsync(revision => revision.AgentflowId == AgentflowId, token)
        );
        Assert.Equal(4, await context.AgentflowNodes.CountAsync(node => node.AgentflowId == AgentflowId, token));
        Assert.Equal(3, await context.AgentflowEdges.CountAsync(edge => edge.AgentflowId == AgentflowId, token));
        var inputRoutingStrategy = await context
            .AgentflowNodes.Where(node => node.AgentflowId == AgentflowId && node.Kind == AgentflowNodeKind.Input)
            .Select(node => node.RoutingStrategy)
            .SingleAsync(token);
        Assert.Equal(AgentflowRoutingStrategy.FanOut, inputRoutingStrategy);
    }

    [Fact]
    public async Task MaintainSystemDataAsync_RepeatedStartup_DoesNotDuplicateOrResetBuiltinRevisions()
    {
        var paths = CreatePaths();
        try
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={Path.Combine(paths.Root, "builtin-revisions.db")}")
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var seeder = new DbSeeder(
                context,
                NullLogger<DbSeeder>.Instance,
                TimeProvider.System,
                paths,
                CreateWorkspaceAccess(paths),
                CreateAuthentication(context),
                [new TestSkillRegistration()]
            );
            var token = TestContext.Current.CancellationToken;
            await seeder.SeedAsync();

            var now = TimeProvider.System.GetUtcNow();
            context.AgentflowRevisions.Add(
                new AgentflowRevision
                {
                    AgentflowId = AgentflowId,
                    Revision = 2,
                    CreateBy = Constants.AdminUserId,
                    CreateTime = now,
                    UpdateBy = Constants.AdminUserId,
                    UpdateTime = now,
                }
            );
            context.AgentflowNodes.Add(
                new AgentflowNode
                {
                    AgentflowId = AgentflowId,
                    AgentflowRevision = 2,
                    NodeId = "revision-2-input",
                    Kind = AgentflowNodeKind.Input,
                    Name = "Input",
                    ConfigJson = "{}",
                    CreateBy = Constants.AdminUserId,
                    CreateTime = now,
                    UpdateBy = Constants.AdminUserId,
                    UpdateTime = now,
                }
            );
            var flow = await context.Agentflows.SingleAsync(item => item.Id == AgentflowId, token);
            flow.LatestRevision = 2;
            flow.PublishedRevision = 2;
            flow.Enable = false;
            await context.SaveChangesAsync(token);
            context.ChangeTracker.Clear();

            var revisionCount = await context.AgentflowRevisions.CountAsync(token);
            var nodeCount = await context.AgentflowNodes.CountAsync(token);
            var edgeCount = await context.AgentflowEdges.CountAsync(token);
            await seeder.MaintainSystemDataAsync();
            await seeder.MaintainSystemDataAsync();
            context.ChangeTracker.Clear();

            var maintained = await context.Agentflows.SingleAsync(item => item.Id == AgentflowId, token);
            Assert.Equal(2, maintained.LatestRevision);
            Assert.Equal(2, maintained.PublishedRevision);
            Assert.False(maintained.Enable);
            Assert.Equal(revisionCount, await context.AgentflowRevisions.CountAsync(token));
            Assert.Equal(nodeCount, await context.AgentflowNodes.CountAsync(token));
            Assert.Equal(edgeCount, await context.AgentflowEdges.CountAsync(token));
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task MaintainSystemDataAsync_BuiltinIdentityConflict_FailsBeforeServing()
    {
        var paths = CreatePaths();
        try
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={Path.Combine(paths.Root, "builtin-owner.db")}")
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var seeder = new DbSeeder(
                context,
                NullLogger<DbSeeder>.Instance,
                TimeProvider.System,
                paths,
                CreateWorkspaceAccess(paths),
                CreateAuthentication(context),
                [new TestSkillRegistration()]
            );
            var token = TestContext.Current.CancellationToken;
            await seeder.SeedAsync();
            await context
                .Agentflows.Where(item => item.Id == AgentflowId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.CreateBy, "another-user"), token);
            context.ChangeTracker.Clear();
            var revisionCount = await context.AgentflowRevisions.CountAsync(token);
            var nodeCount = await context.AgentflowNodes.CountAsync(token);
            var edgeCount = await context.AgentflowEdges.CountAsync(token);

            var exception = await Assert.ThrowsAsync<AgwException>(() => seeder.MaintainSystemDataAsync());

            Assert.Equal(ErrorCodes.AgentflowDefinitionInvalid.Code, exception.Code);
            Assert.Contains(AgentflowId.ToString(), exception.Message, StringComparison.Ordinal);
            Assert.Equal(
                "another-user",
                await context
                    .Agentflows.Where(item => item.Id == AgentflowId)
                    .Select(item => item.CreateBy)
                    .SingleAsync(token)
            );
            Assert.Equal(revisionCount, await context.AgentflowRevisions.CountAsync(token));
            Assert.Equal(nodeCount, await context.AgentflowNodes.CountAsync(token));
            Assert.Equal(edgeCount, await context.AgentflowEdges.CountAsync(token));
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task MaintainSystemDataAsync_BuiltinAgentMissing_FailsBeforePublishing()
    {
        var paths = CreatePaths();
        try
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={Path.Combine(paths.Root, "builtin-agent-missing.db")}")
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var seeder = new DbSeeder(
                context,
                NullLogger<DbSeeder>.Instance,
                TimeProvider.System,
                paths,
                CreateWorkspaceAccess(paths),
                CreateAuthentication(context),
                [new TestSkillRegistration()]
            );
            var token = TestContext.Current.CancellationToken;
            await seeder.SeedAsync();
            await context.AgentflowEdges.Where(item => item.AgentflowId == AgentflowId).ExecuteDeleteAsync(token);
            await context.AgentflowNodes.Where(item => item.AgentflowId == AgentflowId).ExecuteDeleteAsync(token);
            await context.AgentflowRevisions.Where(item => item.AgentflowId == AgentflowId).ExecuteDeleteAsync(token);
            await context.Agentflows.Where(item => item.Id == AgentflowId).ExecuteDeleteAsync(token);
            await context.Agents.Where(item => item.Id == LocationExtractorAgentId).ExecuteDeleteAsync(token);
            context.ChangeTracker.Clear();

            var exception = await Assert.ThrowsAsync<AgwException>(() => seeder.MaintainSystemDataAsync());

            Assert.Equal(ErrorCodes.AgentflowDefinitionInvalid.Code, exception.Code);
            Assert.Contains(LocationExtractorAgentId.ToString(), exception.Message, StringComparison.Ordinal);
            Assert.Empty(await context.Agentflows.ToListAsync(token));
            Assert.Empty(await context.AgentflowRevisions.ToListAsync(token));
            Assert.Empty(await context.AgentflowNodes.ToListAsync(token));
            Assert.Empty(await context.AgentflowEdges.ToListAsync(token));
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeedAsync_BuiltInProjects_CreatesWorkspaceDirectoriesAndPreservesFiles(bool isolationEnabled)
    {
        // Arrange
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
                CreateWorkspaceAccess(paths, isolationEnabled),
                CreateAuthentication(context),
                [new TestSkillRegistration()]
            );
            var existingA2A = isolationEnabled
                ? Path.Combine(paths.UserHomeDirectory, ".agw", "projects", ProjectDefaults.A2AId.ToString("N"))
                : Path.Combine(paths.UserHomeDirectory, ".agw", "a2a");
            Directory.CreateDirectory(existingA2A);
            File.WriteAllText(Path.Combine(existingA2A, "existing.txt"), "existing");

            // Act
            await seeder.SeedAsync();
            var workspaces = await context.Projects.ToDictionaryAsync(
                project => project.Name,
                project => project.Workspace,
                TestContext.Current.CancellationToken
            );
            foreach (var name in new[] { "default-built-in", "a2a" })
            {
                File.WriteAllText(Path.Combine(workspaces[name]!, "seeded.txt"), name);
            }

            await seeder.SeedAsync();

            // Assert
            var projects = await context.Projects.ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, projects.Count);
            foreach (var name in new[] { "default-built-in", "a2a" })
            {
                var project = Assert.Single(projects, project => project.Name == name);
                var expectedWorkspace = isolationEnabled
                    ? Path.Combine(paths.UserHomeDirectory, ".agw", "projects", project.Id.ToString("N"))
                    : Path.Combine(paths.UserHomeDirectory, ".agw", name);
                Assert.Equal(expectedWorkspace, project.Workspace);
                Assert.Equal(name, File.ReadAllText(Path.Combine(expectedWorkspace, "seeded.txt")));
            }

            Assert.Equal("existing", File.ReadAllText(Path.Combine(existingA2A, "existing.txt")));
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
                CreateWorkspaceAccess(paths),
                CreateAuthentication(context),
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
                CreateWorkspaceAccess(paths),
                CreateAuthentication(context),
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
                CreateWorkspaceAccess(paths),
                CreateAuthentication(context),
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
    public async Task SeedAsync_ExistingAgentTools_PreservesConfiguration(bool includesFileAccess)
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
                    Tools = CreateGeneralAgentTools(includesFileAccess),
                    CreateBy = Constants.AdminUserId,
                },
                new Agent
                {
                    Id = LocationExtractorAgentId,
                    Name = "location-extractor",
                    DisplayName = "Location Extractor",
                    Type = AgentType.System,
                    CreateBy = Constants.AdminUserId,
                    Tools = [new ToolValue { Definition = new WebFetchToolDefinition() }],
                }
            );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            var seeder = new DbSeeder(
                context,
                NullLogger<DbSeeder>.Instance,
                TimeProvider.System,
                paths,
                CreateWorkspaceAccess(paths),
                CreateAuthentication(context),
                [new TestSkillRegistration()]
            );

            await seeder.SeedAsync();
            await seeder.SeedAsync();

            var general = await context.Agents.SingleAsync(
                agent => agent.Id == GeneralAgentId,
                TestContext.Current.CancellationToken
            );
            Assert.Equal(
                ToolValueObjectJson.Serialize(CreateGeneralAgentTools(includesFileAccess)),
                ToolValueObjectJson.Serialize(general.Tools)
            );
            var location = await context.Agents.SingleAsync(
                agent => agent.Id == LocationExtractorAgentId,
                TestContext.Current.CancellationToken
            );
            Assert.Collection(
                location.Tools,
                value => Assert.IsType<WebFetchToolDefinition>(Assert.IsType<ToolValue>(value).Definition)
            );
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    private static void AddBuiltInAgentReferences(AgwDbContext context)
    {
        context.Agents.AddRange(
            new Agent
            {
                Id = GeneralAgentId,
                Name = "general-agent",
                DisplayName = "General Agent",
                Type = AgentType.System,
                CreateBy = Constants.AdminUserId,
            },
            new Agent
            {
                Id = LocationExtractorAgentId,
                Name = "location-extractor",
                DisplayName = "Location Extractor",
                Type = AgentType.System,
                CreateBy = Constants.AdminUserId,
            },
            new Agent
            {
                Id = AmapPoiSearchAgentId,
                Name = "amap-poi-search",
                DisplayName = "Amap POI Search",
                Type = AgentType.System,
                CreateBy = Constants.AdminUserId,
            }
        );
    }

    private static List<ToolValueObject> CreateGeneralAgentTools(bool includesFileAccess)
    {
        List<ToolValueObject> tools =
        [
            new ToolValue { Definition = new DiffToolDefinition() },
            new ToolValue { Definition = new GitCloneToolDefinition() },
            new ToolValue { Definition = new RunShellToolDefinition() },
        ];
        if (includesFileAccess)
        {
            tools.Add(new ToolBlockValue { Definition = new FileAccessToolBlockDefinition() });
        }

        return tools;
    }

    private static IProjectWorkspaceAccess CreateWorkspaceAccess(AgwDataPaths paths, bool isolationEnabled = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{ProjectWorkspaceOptions.SectionName}:IsolationEnabled"] = isolationEnabled.ToString(),
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddFiles(configuration);
        services.AddProjects(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IProjectWorkspaceAccess>();
    }

    /// <summary>
    /// 数据目录与用户目录都位于测试输出目录的 .scratch 下；用户目录在数据目录内，随数据目录一起删除。
    /// Both the data root and the user home live under .scratch in the test output directory; the home is inside the
    /// data root and is deleted with it.
    /// </summary>
    private static AgwDataPaths CreatePaths()
    {
        var root = Path.Combine(AppContext.BaseDirectory, ".scratch", "db-seeder", Guid.CreateVersion7().ToString("N"));
        var paths = AgwDataPaths.Resolve(root, Path.Combine(root, "home"));
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
