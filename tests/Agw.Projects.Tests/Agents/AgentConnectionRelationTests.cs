using System.Security.Claims;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Infrastructure.Data;
using Agw.Integrations.Application.Facades;
using Agw.Projects.Tests;
using Agw.Providers.Application.Facades;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;
using Agw.Skills.Application.Facades;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public class AgentConnectionRelationTests : IDisposable
{
    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], authenticationType: "Test")
        )
    );

    public void Dispose() => _userScope.Dispose();

    [Fact]
    public async Task CreateAgentAsync_WhenConnectionIdsContainUnknownValues_ThrowsInvalidParam()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await TestScope.CreateAsync(cancellationToken);
        var agent = CreateAgent(Guid.CreateVersion7());

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Service.CreateAgentAsync(
                agent,
                mcpToolServerIds: null,
                skillIds: null,
                connectionIds: [scope.FirstConnectionId, scope.FirstConnectionId, Guid.Empty, Guid.CreateVersion7()]
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task UpdateAgentAsync_WhenSystemConnectionIdsChange_ReplacesRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await TestScope.CreateAsync(cancellationToken, AgentType.System);

        var updated = await scope.Service.UpdateAgentAsync(
            scope.AgentId,
            new AgentUpdateRequest
            {
                DisplayName = "Updated agent",
                Description = "desc",
                SystemPrompt = "prompt",
                ModelProviderId = scope.ModelProviderId,
                ConnectionIds = [scope.SecondConnectionId],
            }.ToCommand()
        );

        Assert.NotNull(updated);
        await using var assertContext = scope.CreateDbContext();
        var relation = Assert.Single(await assertContext.AgentConnectionRelations.ToListAsync(cancellationToken));
        Assert.Equal(scope.SecondConnectionId, relation.ConnectionId);
    }

    [Fact]
    public async Task UpdateAgentAsync_WhenConnectionIdsOmitted_PreservesOwnerOverlay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await TestScope.CreateAsync(cancellationToken, AgentType.System);

        await scope.Service.UpdateAgentAsync(
            scope.AgentId,
            new AgentUpdateRequest
            {
                DisplayName = "Updated agent",
                Description = "desc",
                SystemPrompt = "prompt",
                ModelProviderId = scope.ModelProviderId,
            }.ToCommand()
        );

        await using var assertContext = scope.CreateDbContext();
        var relation = Assert.Single(await assertContext.AgentConnectionRelations.ToListAsync(cancellationToken));
        Assert.Equal(scope.FirstConnectionId, relation.ConnectionId);
    }

    [Fact]
    public async Task UpdateAgentAsync_WhenConnectionIdsExplicitlyNull_ClearsOwnerOverlay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await TestScope.CreateAsync(cancellationToken, AgentType.System);

        await scope.Service.UpdateAgentAsync(
            scope.AgentId,
            new AgentUpdateRequest
            {
                DisplayName = "Updated agent",
                Description = "desc",
                SystemPrompt = "prompt",
                ModelProviderId = scope.ModelProviderId,
                ConnectionIds = null,
            }.ToCommand()
        );

        await using var assertContext = scope.CreateDbContext();
        Assert.Empty(await assertContext.AgentConnectionRelations.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task ListAndGetAgentAsync_WhenAgentHasConnection_ReturnsIncludedRelation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await TestScope.CreateAsync(cancellationToken);

        var listed = Assert.Single(await scope.Service.ListAgentsAsync(), agent => agent.Id == scope.AgentId);
        var fetched = await scope.Service.GetAgentAsync(scope.AgentId);

        Assert.Equal(scope.FirstConnectionId, Assert.Single(listed.AgentConnectionRelations).ConnectionId);
        Assert.NotNull(fetched);
        Assert.Equal(scope.FirstConnectionId, Assert.Single(fetched.AgentConnectionRelations).ConnectionId);
    }

    [Fact]
    public async Task UserScopedRelations_ForeignUserCannotViewAgentAndOwnerUpdatePreservesForeignRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await TestScope.CreateAsync(cancellationToken, AgentType.System);
        var foreignConnectionId = Guid.CreateVersion7();
        await using (var seedContext = scope.CreateDbContext())
        {
            seedContext.Connections.Add(CreateConnection(foreignConnectionId, "foreign-github", "other-user"));
            seedContext.AgentConnectionRelations.Add(
                new AgentConnectionRelation { AgentId = scope.AgentId, ConnectionId = foreignConnectionId }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var ownView = await scope.Service.GetAgentForCurrentUserAsync(scope.AgentId);
        scope.UserInfo.UserId = "other-user";
        var foreignView = await scope.Service.GetAgentForCurrentUserAsync(scope.AgentId);
        scope.UserInfo.UserId = "tester";
        var updated = await scope.Service.UpdateAgentAsync(
            scope.AgentId,
            new AgentUpdateRequest
            {
                DisplayName = "Updated agent",
                Description = "desc",
                SystemPrompt = "prompt",
                ModelProviderId = scope.ModelProviderId,
                ConnectionIds = [scope.SecondConnectionId],
            }.ToCommand()
        );

        Assert.NotNull(ownView);
        Assert.Equal(scope.FirstConnectionId, Assert.Single(ownView.AgentConnectionRelations).ConnectionId);
        Assert.Null(foreignView);
        Assert.NotNull(updated);
        await using var assertContext = scope.CreateDbContext();
        using var systemScope = UserInfoUtil.PushSystemScope();
        Assert.Equal(
            new[] { foreignConnectionId, scope.SecondConnectionId }.OrderBy(id => id),
            (
                await assertContext
                    .AgentConnectionRelations.Select(relation => relation.ConnectionId)
                    .ToListAsync(cancellationToken)
            ).OrderBy(id => id)
        );
    }

    private static Agent CreateAgent(Guid id, AgentType type = AgentType.External, Guid? modelProviderId = null) =>
        new()
        {
            Id = id,
            Name = $"agent-{id:N}",
            DisplayName = "Agent",
            Description = "desc",
            SystemPrompt = "prompt",
            Type = type,
            ModelProviderId = modelProviderId,
            CreateBy = "tester",
        };

    private static Connection CreateConnection(Guid id, string alias, string user = "tester") =>
        new()
        {
            Id = id,
            PluginId = "github",
            ConnectorId = "github-cloud",
            AuthSchemeId = "oauth",
            DisplayName = alias,
            Alias = alias,
            Status = ConnectionStatus.Ready,
            CreateBy = user,
        };

    private sealed class TestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly AgwDbContext _serviceContext;

        private TestScope(
            SqliteConnection connection,
            DbContextOptions<AgwDbContext> options,
            AgwDbContext serviceContext,
            AgentAppService service,
            TestUserInfoService userInfo
        )
        {
            _connection = connection;
            _options = options;
            _serviceContext = serviceContext;
            Service = service;
            UserInfo = userInfo;
        }

        public AgentAppService Service { get; }
        public TestUserInfoService UserInfo { get; }
        public Guid AgentId { get; private init; }
        public Guid FirstConnectionId { get; private init; }
        public Guid SecondConnectionId { get; private init; }
        public Guid ModelProviderId { get; private init; }

        public static async Task<TestScope> CreateAsync(
            CancellationToken cancellationToken,
            AgentType agentType = AgentType.External
        )
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            await using (var setupContext = new AgwDbContext(options))
            {
                await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            }

            var agentId = Guid.CreateVersion7();
            var firstConnectionId = Guid.CreateVersion7();
            var secondConnectionId = Guid.CreateVersion7();
            var modelId = Guid.CreateVersion7();
            var providerId = Guid.CreateVersion7();
            var modelProviderId = Guid.CreateVersion7();
            await using (var seedContext = new AgwDbContext(options))
            {
                seedContext.Models.Add(
                    new AgwAiModel
                    {
                        Id = modelId,
                        Name = "test-model",
                        CreateBy = "tester",
                    }
                );
                seedContext.Providers.Add(
                    new Provider
                    {
                        Id = providerId,
                        Name = "test-provider",
                        Endpoint = "https://example.test",
                        CreateBy = "tester",
                    }
                );
                seedContext.ModelProviders.Add(
                    new ModelProviderRelation
                    {
                        Id = modelProviderId,
                        ModelId = modelId,
                        ProviderId = providerId,
                        CreateBy = "tester",
                    }
                );
                seedContext.Agents.Add(CreateAgent(agentId, agentType, modelProviderId));
                seedContext.Connections.AddRange(
                    CreateConnection(firstConnectionId, "work-github"),
                    CreateConnection(secondConnectionId, "personal-github")
                );
                seedContext.AgentConnectionRelations.Add(
                    new AgentConnectionRelation { AgentId = agentId, ConnectionId = firstConnectionId }
                );
                await seedContext.SaveChangesAsync(cancellationToken);
            }

            var serviceContext = new AgwDbContext(options);
            var userInfo = new TestUserInfoService();
            return new TestScope(connection, options, serviceContext, CreateService(serviceContext, userInfo), userInfo)
            {
                AgentId = agentId,
                FirstConnectionId = firstConnectionId,
                SecondConnectionId = secondConnectionId,
                ModelProviderId = modelProviderId,
            };
        }

        public AgwDbContext CreateDbContext() => new(_options);

        public async ValueTask DisposeAsync()
        {
            await _serviceContext.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static AgentAppService CreateService(AgwDbContext dbContext, TestUserInfoService userInfo) =>
            new AgentAppService(
                dbContext,
                new ConnectionReferenceFacade(dbContext, userInfo),
                new ModelProviderReferenceFacade(dbContext, userInfo),
                new SkillReferenceFacade(dbContext, userInfo),
                userInfo
            );
    }
}
