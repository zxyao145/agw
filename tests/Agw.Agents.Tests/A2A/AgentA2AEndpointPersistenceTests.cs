using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Definitions.Agents;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests.A2A;

public sealed class AgentA2AEndpointPersistenceTests : IDisposable
{
    private const string Origin = "https://a.example:443";
    private const string Headers = """{"Authorization":"Bearer secret-1"}""";
    private readonly string _databasePath = Path.Combine(AppContext.BaseDirectory, $"a2a-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task SaveChanges_EndpointCredentials_StoredProtectedAndReadBack()
    {
        using var scope = UseOwner();
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await using var db = CreateContext();

        var raw = await db
            .Database.SqlQueryRaw<string>("SELECT credentials AS Value FROM agent_a2a_endpoint")
            .SingleAsync(ct);
        using var stored = JsonDocument.Parse(raw);
        Assert.Equal(JsonValueKind.String, stored.RootElement.GetProperty(Origin).ValueKind);
        Assert.DoesNotContain("secret-1", raw);
        var endpoint = await db.AgentA2AEndpoints.SingleAsync(ct);
        var decrypted = A2AEndpointCredentialsJson.Deserialize(endpoint.Credentials);
        Assert.Equal("Bearer secret-1", decrypted.HeadersByOrigin[Origin]["Authorization"]);

        endpoint.Credentials[Origin] = """{"Authorization":"Bearer secret-2"}""";
        await db.SaveChangesAsync(ct);
        await using var updatedDb = CreateContext();
        var updated = await updatedDb.AgentA2AEndpoints.SingleAsync(ct);
        Assert.Equal(endpoint.Credentials[Origin], updated.Credentials[Origin]);
        var updatedRaw = await updatedDb
            .Database.SqlQueryRaw<string>("SELECT credentials AS Value FROM agent_a2a_endpoint")
            .SingleAsync(ct);
        Assert.DoesNotContain("secret-2", updatedRaw);
    }

    [Fact]
    public async Task SaveChanges_Extra_UsesCamelCaseKeys()
    {
        using var scope = UseOwner();
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        await using var db = CreateContext();

        var raw = await db
            .Database.SqlQueryRaw<string>("SELECT extra AS Value FROM agent_a2a_endpoint")
            .SingleAsync(ct);
        using var stored = JsonDocument.Parse(raw);
        var extra = stored.RootElement;
        Assert.Equal("n", extra.GetProperty("card").GetProperty("name").GetString());
        Assert.Empty(extra.GetProperty("card").GetProperty("supportedInterfaces").EnumerateArray());
        Assert.Equal(
            new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero),
            extra.GetProperty("cardFetchedAt").GetDateTimeOffset()
        );
    }

    [Fact]
    public async Task Query_AgentWithInclude_LoadsEndpoint()
    {
        using var scope = UseOwner();
        var ct = TestContext.Current.CancellationToken;
        var agentId = await SeedAsync(ct);
        await using var db = CreateContext();

        var agent = await db.Agents.Include(agent => agent.A2AEndpoint).SingleAsync(ct);
        Assert.Equal(agentId, agent.A2AEndpoint?.AgentId);
        Assert.Equal("https://a.example/card", agent.A2AEndpoint?.CardUrl);
    }

    [Fact]
    public async Task Query_EndpointOfAnotherUser_ExcludesCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        using (UseOwner())
        {
            await SeedAsync(ct);
        }

        using var scope = UseOwner("another");
        await using var db = CreateContext();
        Assert.Empty(await db.AgentA2AEndpoints.ToListAsync(ct));
    }

    private async Task<Guid> SeedAsync(CancellationToken ct)
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync(ct);
        using var card = JsonDocument.Parse("""{"name":"n","supportedInterfaces":[]}""");
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = "a2a",
            Type = AgentType.External,
            ExternalAgentKind = EngineKind.A2A,
            CreateBy = "owner",
            A2AEndpoint = new AgentA2AEndpoint
            {
                CardUrl = "https://a.example/card",
                InterfaceUrl = "https://a.example/agent",
                ProtocolBinding = "HTTP+JSON",
                InterfaceRevision = Guid.CreateVersion7(),
                Credentials = new Dictionary<string, string> { [Origin] = Headers },
                Extra = new AgentA2AEndpointExtra
                {
                    Card = card.RootElement.Clone(),
                    CardFetchedAt = new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero),
                },
            },
        };
        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);
        return agent.Id;
    }

    private AgwDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<AgwDbContext>().UseSqlite($"Data Source={_databasePath};Pooling=False").Options
        );

    private static IDisposable UseOwner(string id = "owner") =>
        UserInfoUtil.Push(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test")));

    public void Dispose() => File.Delete(_databasePath);
}
