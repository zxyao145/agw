using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tools.Tests;

public sealed class ToolValueObjectJsonTests
{
    [Fact]
    public void Serialize_ToolAndToolBlock_WritesDoublePolymorphicContract()
    {
        ToolValueObject[] values =
        [
            new ToolValue { Definition = new WebSearchToolDefinition() },
            new ToolBlockValue
            {
                Definition = new ProjectMemoryToolBlockDefinition
                {
                    Options = new ProjectMemoryToolBlockOptions { Storage = ProjectMemoryStorage.Database },
                },
            },
        ];

        var json = ToolValueObjectJson.Serialize(values);

        Assert.Equal(
            """[{"kind":"tool","definition":{"name":"web_search","options":{}}},{"kind":"toolBlock","definition":{"name":"project-memory","options":{"storage":"database"}}}]""",
            json
        );
    }

    [Fact]
    public void Deserialize_BackgroundAgentsOptions_CreatesStrongDefinition()
    {
        var agentId = Guid.NewGuid();
        var json =
            """[{"kind":"toolBlock","definition":{"name":"background-agents","options":{"allowedAgentIds":["""
            + $"\"{agentId}\""
            + """]}}}]""";

        var values = ToolValueObjectJson.Deserialize(json);

        var value = Assert.IsType<ToolBlockValue>(Assert.Single(values));
        var definition = Assert.IsType<BackgroundAgentsToolBlockDefinition>(value.Definition);
        Assert.Equal([agentId], definition.Options.AllowedAgentIds);
    }

    [Fact]
    public void Deserialize_UserMemory_CreatesParameterlessDefinition()
    {
        var values = ToolValueObjectJson.Deserialize(
            """[{"kind":"toolBlock","definition":{"name":"user-memory","options":{}}}]"""
        );

        var value = Assert.IsType<ToolBlockValue>(Assert.Single(values));
        Assert.IsType<UserMemoryToolBlockDefinition>(value.Definition);
    }

    [Fact]
    public void Deserialize_LegacyToolNames_MapsToStrongDefinitions()
    {
        var values = ToolValueObjectJson.Deserialize("""["WEB_SEARCH","web_fetch"]""");

        Assert.Collection(
            values,
            value => Assert.IsType<WebSearchToolDefinition>(Assert.IsType<ToolValue>(value).Definition),
            value => Assert.IsType<WebFetchToolDefinition>(Assert.IsType<ToolValue>(value).Definition)
        );
        Assert.Equal(
            """[{"kind":"tool","definition":{"name":"web_search","options":{}}},{"kind":"tool","definition":{"name":"web_fetch","options":{}}}]""",
            ToolValueObjectJson.Serialize(values)
        );
    }

    [Theory]
    [InlineData("""[{"kind":"unknown","definition":{"name":"web_search","options":{}}}]""")]
    [InlineData("""[{"kind":"tool","definition":{"name":"unknown","options":{}}}]""")]
    [InlineData("""[{"kind":"tool","definition":{"name":"project-memory","options":{"storage":"database"}}}]""")]
    [InlineData("""[{"kind":"toolBlock","definition":{"name":"project-memory","options":"database"}}]""")]
    [InlineData("""[{"kind":"tool","definition":{"name":"web_search"}}]""")]
    public void Deserialize_InvalidTypedValue_ThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => ToolValueObjectJson.Deserialize(json));
    }

    [Fact]
    public void Deserialize_UnknownLegacyToolName_ThrowsWithoutChangingSource()
    {
        const string json = """["unknown_tool"]""";

        var exception = Assert.Throws<JsonException>(() => ToolValueObjectJson.Deserialize(json));

        Assert.Contains("unknown_tool", exception.Message, StringComparison.Ordinal);
        Assert.Equal("""["unknown_tool"]""", json);
    }

    [Fact]
    public void ToolDefinitions_JsonDerivedTypesMatchStableNames()
    {
        var mappings = typeof(ToolDefinition)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .ToArray();

        Assert.Equal(
            ToolDefinitionNames.All.Order(StringComparer.Ordinal),
            mappings
                .Select(static mapping => Assert.IsType<string>(mapping.TypeDiscriminator))
                .Order(StringComparer.Ordinal)
        );
        Assert.All(
            mappings,
            mapping =>
            {
                var definition = Assert.IsAssignableFrom<ToolDefinition>(Activator.CreateInstance(mapping.DerivedType));
                Assert.Equal(mapping.TypeDiscriminator, definition.GetDefinitionName());
            }
        );
    }

    [Fact]
    public void ToolBlockDefinitions_JsonDerivedTypesMatchStableNames()
    {
        var mappings = typeof(ToolBlockDefinition)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .ToArray();

        Assert.Equal(
            ToolBlockDefinitionNames.All.Order(StringComparer.Ordinal),
            mappings
                .Select(static mapping => Assert.IsType<string>(mapping.TypeDiscriminator))
                .Order(StringComparer.Ordinal)
        );
        Assert.All(
            mappings,
            mapping =>
            {
                var definition = Assert.IsAssignableFrom<ToolBlockDefinition>(
                    Activator.CreateInstance(mapping.DerivedType)
                );
                Assert.Equal(mapping.TypeDiscriminator, definition.GetDefinitionName());
            }
        );
    }

    [Fact]
    public async Task EfConverter_LegacyToolNames_ReadAndWriteBackAsTypedValues()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var dbContext = CreateDbContext(connection);
        await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var agentId = Guid.CreateVersion7();
        dbContext.Agents.Add(
            new Agent
            {
                Id = agentId,
                DisplayName = "Legacy tools",
                Name = $"legacy-tools-{agentId:N}",
                Type = AgentType.External,
            }
        );
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE agent SET tools = '[\"web_search\"]' WHERE id = {agentId}",
            TestContext.Current.CancellationToken
        );
        dbContext.ChangeTracker.Clear();

        var agent = await dbContext.Agents.SingleAsync(
            item => item.Id == agentId,
            TestContext.Current.CancellationToken
        );
        Assert.IsType<WebSearchToolDefinition>(Assert.IsType<ToolValue>(Assert.Single(agent.Tools)).Definition);

        agent.Tools.Add(new ToolValue { Definition = new WebFetchToolDefinition() });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tools FROM agent WHERE id = $id";
        command.Parameters.AddWithValue("$id", agentId);
        var storedJson = Assert.IsType<string>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            """[{"kind":"tool","definition":{"name":"web_search","options":{}}},{"kind":"tool","definition":{"name":"web_fetch","options":{}}}]""",
            storedJson
        );
    }

    [Fact]
    public async Task EfConverter_UnknownLegacyToolName_FailsAndPreservesStoredValue()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var dbContext = CreateDbContext(connection);
        await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var agentId = Guid.CreateVersion7();
        dbContext.Agents.Add(
            new Agent
            {
                Id = agentId,
                DisplayName = "Unknown legacy tool",
                Name = $"unknown-legacy-tool-{agentId:N}",
                Type = AgentType.External,
            }
        );
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE agent SET tools = '[\"unknown_tool\"]' WHERE id = {agentId}",
            TestContext.Current.CancellationToken
        );
        dbContext.ChangeTracker.Clear();

        var exception = await Assert.ThrowsAsync<JsonException>(async () =>
            await dbContext.Agents.SingleAsync(item => item.Id == agentId, TestContext.Current.CancellationToken)
        );
        Assert.Contains("unknown_tool", exception.Message, StringComparison.Ordinal);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tools FROM agent WHERE id = $id";
        command.Parameters.AddWithValue("$id", agentId);
        Assert.Equal(
            """["unknown_tool"]""",
            Assert.IsType<string>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))
        );
    }

    private static AgwDbContext CreateDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AgwDbContext(options);
    }
}
