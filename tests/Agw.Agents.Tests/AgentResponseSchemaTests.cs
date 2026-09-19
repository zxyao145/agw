using System.Text.Json;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public sealed class AgentResponseSchemaTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("\n\t ", null)]
    [InlineData("  {\"type\":\"object\"}  ", "{\"type\":\"object\"}")]
    public void Normalize_ValidInput_PreservesObjectOrClearsBlank(string? input, string? expected) =>
        Assert.Equal(expected, AgentResponseSchema.Normalize(input));

    [Theory]
    [InlineData("not-json")]
    [InlineData("{invalid")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void Normalize_NonObject_ThrowsSharedError(string input) =>
        Assert.Equal(
            ErrorCodes.InvalidParam.Code,
            Assert.Throws<AgwException>(() => AgentResponseSchema.Normalize(input)).Code
        );

    [Fact]
    public void FromDomain_AgentResponse_IncludesResponseSchema()
    {
        var agent = new Agent { Id = Guid.CreateVersion7(), ResponseSchema = "{\"type\":\"object\"}" };

        Assert.Equal("{\"type\":\"object\"}", AgentResponse.FromDomain(agent).ResponseSchema);
    }

    [Fact]
    public void FromDomain_AgentListResponse_OmitsResponseSchema()
    {
        var agent = new Agent { Id = Guid.CreateVersion7(), ResponseSchema = "{\"type\":\"object\"}" };

        var json = JsonSerializer.Serialize(AgentListResponse.FromDomain(agent), JsonOptions);

        Assert.DoesNotContain("responseSchema", json);
        Assert.Contains("\"displayName\"", json);
        Assert.Contains("\"enable\"", json);
    }

    [Fact]
    public void FromDomain_AgentResponse_SerializesResponseSchema()
    {
        var agent = new Agent { Id = Guid.CreateVersion7(), ResponseSchema = "{\"type\":\"object\"}" };

        var json = JsonSerializer.Serialize(AgentResponse.FromDomain(agent), JsonOptions);
        using var document = JsonDocument.Parse(json);

        Assert.Equal("{\"type\":\"object\"}", document.RootElement.GetProperty("responseSchema").GetString());
    }
}
