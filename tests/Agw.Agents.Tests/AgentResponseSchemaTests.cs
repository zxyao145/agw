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
        Assert.Contains("\"resultFormat\":\"json\"", json);
    }

    [Theory]
    [InlineData(null, ResultFormat.Markdown)]
    [InlineData("", ResultFormat.Markdown)]
    [InlineData(" \n\t", ResultFormat.Markdown)]
    [InlineData("{\"type\":\"object\"}", ResultFormat.Json)]
    public void FromDomain_AgentResponses_ReportSameResultFormatForEveryAgentKind(string? schema, ResultFormat expected)
    {
        foreach (var kind in Enum.GetValues<ExternalAgentKind>())
        {
            var agent = new Agent
            {
                Id = Guid.CreateVersion7(),
                Type = kind == ExternalAgentKind.None ? AgentType.System : AgentType.External,
                ExternalAgentKind = kind,
                ResponseSchema = schema,
            };
            var response = AgentListResponse.FromDomain(agent);
            var detail = AgentResponse.FromDomain(agent);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, JsonOptions));
            using var detailDocument = JsonDocument.Parse(JsonSerializer.Serialize(detail, JsonOptions));

            Assert.Equal(expected, response.ResultFormat);
            Assert.Equal(expected, detail.ResultFormat);
            Assert.Equal(
                document.RootElement.GetProperty("resultFormat").GetString(),
                detailDocument.RootElement.GetProperty("resultFormat").GetString()
            );
            Assert.Equal(
                expected.ToString().ToLowerInvariant(),
                document.RootElement.GetProperty("resultFormat").GetString()
            );
            Assert.False(document.RootElement.TryGetProperty("hasResponseSchema", out _));
            Assert.False(document.RootElement.TryGetProperty("responseSchema", out _));
        }
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
