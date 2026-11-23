using System.Security.Claims;
using System.Text.Json;
using global::A2A;
using Agw.Agents.Application.A2A;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Auth.Application;
using Agw.Host.Data;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Integrations.Application.Facades;
using Agw.Providers.Application.Facades;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Skills.Application.Facades;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests.A2A;

public sealed class A2AAgentDefinitionTests
{
    private const string Authorization = "Bearer card-secret";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateAgent_A2A_StoresEndpointAndEncryptsCredentials()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var owner = UseUser("owner");
        await using var server = await StartAsync(requireCredentials: true);
        var created = await fixture.CreateAgentAsync(server, Authorization, extra: "{ }");

        var rawCredentials = await fixture
            .Db.Database.SqlQuery<string>(
                $"SELECT credentials AS Value FROM agent_a2a_endpoint WHERE agent_id = {created.Id}"
            )
            .SingleAsync(Ct);
        using var stored = JsonDocument.Parse(rawCredentials);

        var saved = Assert.IsType<Agent>(await fixture.Service.GetAgentForCurrentUserAsync(created.Id));
        var response = AgentResponse.FromDomain(saved);
        var endpoint = Assert.IsType<A2AEndpointDto>(response.A2AEndpoint);
        var credentials = Assert.Single(endpoint.CredentialHeaderNames);
        Assert.Equal(A2AEndpointRules.NormalizeOrigin(server.CardUrl), credentials.Origin);
        Assert.Equal(JsonValueKind.String, stored.RootElement.GetProperty(credentials.Origin).ValueKind);
        Assert.DoesNotContain(Authorization, rawCredentials);
        var savedEndpoint = Assert.IsType<AgentA2AEndpoint>(saved.A2AEndpoint);
        var decrypted = A2AEndpointCredentialsJson.Deserialize(savedEndpoint.Credentials);
        Assert.Equal(Authorization, decrypted.HeadersByOrigin[credentials.Origin]["Authorization"]);
        Assert.Equal(["Authorization"], credentials.Names);
        Assert.Equal(server.InterfaceUrl.AbsoluteUri, endpoint.InterfaceUrl);
        Assert.True(endpoint.Streaming);
        Assert.Equal("test-agent", endpoint.Extra.Card.GetProperty("name").GetString());
        Assert.Equal("{}", response.Extra);
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"a2aEndpoint\"", json);
        Assert.Contains(""" "extra":{"card": """.Trim(), json);
        Assert.Contains("cardFetchedAt", json);
        Assert.DoesNotContain(Authorization, json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("<html></html>")]
    public async Task CreateAgent_InvalidCard_SavesNothing(string? cardBody)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var owner = UseUser("owner");
        await using var server = await StartAsync();
        server.Card.SupportedInterfaces.Clear();
        server.CardBodyOverride = cardBody;

        var error = await Assert.ThrowsAsync<AgwException>(() => fixture.CreateAgentAsync(server));

        Assert.Equal(ErrorCodes.A2AAgentCardInvalid.Code, error.Code);
        Assert.False(await fixture.Db.Agents.AnyAsync(Ct));
    }

    [Fact]
    public async Task CreateAgent_A2AWithoutEndpoint_ThrowsInvalidParam()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var owner = UseUser("owner");

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.CreateAgentAsync(NewAgent(), new(), Ct)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.False(await fixture.Db.Agents.AnyAsync(Ct));
    }

    [Fact]
    public async Task UpdateAgent_CredentialsWithKeptValue_RefetchesCardAndStampsAgent()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var owner = UseUser("owner");
        await using var server = await StartAsync(requireCredentials: true);
        var created = await fixture.CreateAgentAsync(server, Authorization);
        var endpoint = Assert.IsType<AgentA2AEndpoint>(created.A2AEndpoint);
        fixture.Clock.SetUtcNow(fixture.Clock.GetUtcNow().AddSeconds(1));

        var updated = await fixture.UpdateAsync(
            created.Id,
            new AgentUpdateRequest
            {
                Extra = "{ }",
                A2AEndpoint = new(
                    null,
                    [
                        new(
                            server.CardUrl.GetLeftPart(UriPartial.Authority),
                            new() { ["authorization"] = null, ["X-Tenant"] = "blue" }
                        ),
                    ]
                ),
            }
        );

        Assert.Equal(fixture.Clock.GetUtcNow(), updated.UpdateTime);
        Assert.Equal("{}", updated.Extra);
        Assert.Equal(endpoint.InterfaceRevision, updated.A2AEndpoint?.InterfaceRevision);
        Assert.Equal(Authorization, server.Requests[1].Headers["Authorization"]);
        Assert.Equal("blue", server.Requests[1].Headers["X-Tenant"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateAgent_CardUrlOnOtherOrigin_NeverReusesOldAuthorization(bool keepOldValue)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var owner = UseUser("owner");
        await using var server = await StartAsync(requireCredentials: true);
        await using var other = await StartAsync();
        var created = await fixture.CreateAgentAsync(server, Authorization);
        var request = new AgentUpdateRequest
        {
            A2AEndpoint = new(
                other.CardUrl.AbsoluteUri,
                keepOldValue ? [OriginCredentials(other.CardUrl, null)] : null
            ),
        };

        if (keepOldValue)
        {
            var error = await Assert.ThrowsAsync<AgwException>(() => fixture.UpdateAsync(created.Id, request));
            Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
            Assert.Empty(other.Requests);
        }
        else
        {
            await fixture.UpdateAsync(created.Id, request);
            Assert.DoesNotContain("Authorization", Assert.Single(other.Requests).Headers.Keys);
        }
    }

    [Theory]
    [InlineData("https://a.example/other", "JSONRPC", true)]
    [InlineData("https://a.example/agent?q=2", "JSONRPC", true)]
    [InlineData("https://a.example/agent#fragment", "JSONRPC", false)]
    [InlineData("HTTPS://A.EXAMPLE:443/agent", "jsonrpc", false)]
    [InlineData("https://a.example/agent", "HTTP+JSON", true)]
    public async Task RefreshCard_InterfaceTargetOrBindingChange_UpdatesRevisionWhenDifferent(
        string url,
        string binding,
        bool rotates
    )
    {
        await using var fixture = await Fixture.CreateAsync();
        using var owner = UseUser("owner");
        await using var server = await StartAsync();
        server.Card.SupportedInterfaces = [Interface("https://a.example/agent", ProtocolBindingNames.JsonRpc)];
        var created = await fixture.CreateAgentAsync(server);
        var revision = Assert.IsType<AgentA2AEndpoint>(created.A2AEndpoint).InterfaceRevision;
        server.Card.SupportedInterfaces = [Interface(url, binding)];
        server.Card.Description = "updated card";
        server.Card.Capabilities.Streaming = false;

        var refreshed = await fixture.Service.RefreshA2ACardAsync(created.Id, Ct);

        var endpoint = Assert.IsType<AgentA2AEndpoint>(Assert.IsType<Agent>(refreshed).A2AEndpoint);
        Assert.Equal(rotates, revision != endpoint.InterfaceRevision);
        Assert.Equal(url, endpoint.InterfaceUrl);
        Assert.Equal(binding.ToUpperInvariant(), endpoint.ProtocolBinding);
        Assert.False(endpoint.Streaming);
        Assert.Equal("updated card", endpoint.Extra.Card.GetProperty("description").GetString());
    }

    [Fact]
    public async Task UpdateAgent_DisplayNameOnly_KeepsEndpointAndSendsNoRequest()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var owner = UseUser("owner");
        await using var server = await StartAsync();
        var created = await fixture.CreateAgentAsync(server);

        var updated = await fixture.UpdateAsync(created.Id, new AgentUpdateRequest { DisplayName = "Renamed" });

        Assert.Equal("Renamed", updated.DisplayName);
        Assert.Equal(created.A2AEndpoint?.InterfaceRevision, updated.A2AEndpoint?.InterfaceRevision);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task PreviewCard_WithAgentIdAndKeptValue_UsesStoredValue()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var owner = UseUser("owner");
        await using var server = await StartAsync(requireCredentials: true);
        var created = await fixture.CreateAgentAsync(server, Authorization);
        server.Card.Description = "preview only";

        var preview = await fixture.Service.PreviewA2ACardAsync(
            new(server.CardUrl.AbsoluteUri, [OriginCredentials(server.CardUrl, null)], created.Id),
            Ct
        );

        Assert.Equal("preview only", preview.Card.GetProperty("description").GetString());
        Assert.Equal(Authorization, server.Requests[1].Headers["Authorization"]);
        var saved = await fixture.Db.AgentA2AEndpoints.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("Test agent", saved.Extra.Card.GetProperty("description").GetString());
    }

    [Fact]
    public async Task PreviewCard_WithoutCardUrl_RejectsRequestEvenWithAgentId()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var owner = UseUser("owner");
        await using var server = await StartAsync();
        var agent = await fixture.CreateAgentAsync(server);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.PreviewA2ACardAsync(new(null!, AgentId: agent.Id), Ct)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task PreviewCard_OtherOriginWithKeptValue_ThrowsInvalidParam()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var owner = UseUser("owner");
        await using var server = await StartAsync(requireCredentials: true);
        await using var other = await StartAsync();
        var created = await fixture.CreateAgentAsync(server, Authorization);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.PreviewA2ACardAsync(
                new(other.CardUrl.AbsoluteUri, [OriginCredentials(other.CardUrl, null)], created.Id),
                Ct
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Empty(other.Requests);
    }

    [Fact]
    public async Task GetAgent_OtherUser_GetsNoDefinitionOrCard()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var server = await StartAsync();
        Guid id;
        using (UseUser("owner"))
        {
            id = (await fixture.CreateAgentAsync(server)).Id;
        }
        using var other = UseUser("other");

        Assert.Null(await fixture.Service.GetAgentForCurrentUserAsync(id));
        Assert.Null(await fixture.Service.RefreshA2ACardAsync(id, Ct));
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.PreviewA2ACardAsync(new(server.CardUrl.AbsoluteUri, null, id), Ct)
        );
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, error.Code);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task RefreshCard_NonA2AAgent_ReturnsNull()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var owner = UseUser("owner");
        var agent = NewAgent();
        agent.ExternalAgentKind = EngineKind.Codex;
        var created = Assert.IsType<Agent>(await fixture.Service.CreateAgentAsync(agent, new(), Ct));

        Assert.Null(await fixture.Service.RefreshA2ACardAsync(created.Id, Ct));
    }

    [Fact]
    public async Task DeleteAgent_A2A_RemovesEndpointRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var owner = UseUser("owner");
        await using var server = await StartAsync();
        var created = await fixture.CreateAgentAsync(server);

        Assert.True(await fixture.Service.DeleteAgentAsync(created.Id, Ct));

        Assert.False(await fixture.Db.AgentA2AEndpoints.IgnoreQueryFilters().AnyAsync(Ct));
    }

    private static Task<A2ATestAgentServer> StartAsync(bool requireCredentials = false) =>
        A2ATestAgentServer.StartAsync(
            new(ProtocolBindingNames.HttpJson, true, ScriptedAgentHandler.Echo())
            {
                RequiredCardHeaders = requireCredentials
                    ? new Dictionary<string, string> { ["Authorization"] = Authorization }
                    : null,
            },
            Ct
        );

    private static Agent NewAgent() =>
        new()
        {
            Name = "remote",
            DisplayName = "Remote",
            Type = AgentType.External,
            ExternalAgentKind = EngineKind.A2A,
        };

    private static AgentInterface Interface(string url, string binding) =>
        new()
        {
            Url = url,
            ProtocolBinding = binding,
            ProtocolVersion = "1.0",
        };

    private static IDisposable UseUser(string id) =>
        UserInfoUtil.Push(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test")));

    private static A2AOriginCredentialsRequest OriginCredentials(Uri url, string? value) =>
        new(url.GetLeftPart(UriPartial.Authority), new() { ["Authorization"] = value });

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        public TestTimeProvider Clock { get; }
        public AgwDbContext Db { get; }
        public AgentAppService Service { get; }

        private Fixture(SqliteConnection connection, ServiceProvider services)
        {
            _connection = connection;
            _services = services;
            Clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero));
            var userInfo = new UserInfoService();
            var auditUser = new EntityAuditUserIdProvider(userInfo);
            Db = new AgwDbContext(
                new DbContextOptionsBuilder<AgwDbContext>()
                    .UseSqlite(connection)
                    .UseSnakeCaseNamingConvention()
                    .AddInterceptors(
                        new EntityCreatorInterceptor(auditUser, Clock),
                        new EntityModifierInterceptor(auditUser, Clock)
                    )
                    .Options
            );
            Service = TestAgentAppService.Create(
                Db,
                new ConnectionReferenceFacade(Db, userInfo),
                new ModelProviderReferenceFacade(Db, userInfo),
                new SkillReferenceFacade(Db, userInfo),
                userInfo,
                new AgentDeletionCoordinator(
                    Db,
                    InMemoryApplicationLock.Shared,
                    TurnPersistenceTestKit.CreateRevisionUsageReader(Db)
                ),
                _services.GetRequiredService<IA2AAgentCardClient>(),
                Clock
            );
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(Ct);
            var fixture = new Fixture(connection, A2AAgentCardClientTests.CreateServices());
            await fixture.Db.Database.EnsureCreatedAsync(Ct);
            return fixture;
        }

        public async Task<Agent> CreateAgentAsync(
            A2ATestAgentServer server,
            string? authorization = null,
            string? extra = null
        )
        {
            var endpoint = new A2AEndpointInput(
                server.CardUrl.AbsoluteUri,
                authorization is null
                    ? null
                    :
                    [
                        new(
                            server.CardUrl.GetLeftPart(UriPartial.Authority),
                            new Dictionary<string, string?> { ["Authorization"] = authorization }
                        ),
                    ]
            );
            var definition = NewAgent();
            definition.Extra = extra;
            var agent = Assert.IsType<Agent>(
                await Service.CreateAgentAsync(definition, new() { A2AEndpoint = endpoint }, Ct)
            );
            Db.ChangeTracker.Clear();
            return agent;
        }

        public async Task<Agent> UpdateAsync(Guid id, AgentUpdateRequest request) =>
            Assert.IsType<Agent>(await Service.UpdateAgentAsync(id, request.ToCommand(), Ct));

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            await _services.DisposeAsync();
        }
    }
}
