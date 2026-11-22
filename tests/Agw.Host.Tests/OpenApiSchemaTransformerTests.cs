using System.Text.Json;
using Agw.Agents.Definitions.Controllers;
using Agw.Host.OpenApi;
using Agw.Notifications.Api.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Host.Tests;

/// <summary>
/// <para>验证 OpenAPI schema 中的必填属性和可空类型。</para>
/// <para>Verifies required properties and nullable values in the OpenAPI schema.</para>
/// </summary>
public sealed class OpenApiSchemaTransformerTests
{
    [Fact]
    public async Task NotificationAnswerRequest_TextOnlyAnswer_OptionSchemaAllowsNull()
    {
        var schemas = await GenerateSchemasAsync();
        var answer = schemas.GetProperty("NotificationAnswerRequest");
        var types = answer
            .GetProperty("properties")
            .GetProperty("option")
            .GetProperty("type")
            .EnumerateArray()
            .Select(type => type.GetString())
            .ToArray();
        Assert.Contains("integer", types);
        Assert.Contains("null", types);
        Assert.DoesNotContain(answer.GetProperty("required").EnumerateArray(), item => item.GetString() == "option");
    }

    [Fact]
    public async Task AgentUpdateRequest_AllPropertiesNullable_HasNoRequiredProperties()
    {
        var schemas = await GenerateSchemasAsync();

        Assert.False(schemas.GetProperty("AgentUpdateRequest").TryGetProperty("required", out _));
    }

    [Fact]
    public async Task A2AOriginCredentialsRequest_HeaderValues_AllowNull()
    {
        var schemas = await GenerateSchemasAsync();

        var types = schemas
            .GetProperty("A2AOriginCredentialsRequest")
            .GetProperty("properties")
            .GetProperty("headers")
            .GetProperty("additionalProperties")
            .GetProperty("type")
            .EnumerateArray()
            .Select(type => type.GetString())
            .ToArray();
        Assert.Contains("null", types);
        Assert.Contains("string", types);
        Assert.False(schemas.GetProperty("A2AEndpointRequest").TryGetProperty("required", out _));
        Assert.Contains(
            schemas.GetProperty("A2ACardPreviewRequest").GetProperty("required").EnumerateArray(),
            property => property.GetString() == "cardUrl"
        );
    }

    [Fact]
    public async Task AgentCreateRequest_RequiredFollowsDeclaredNullability()
    {
        var schemas = await GenerateSchemasAsync();

        var required = schemas
            .GetProperty("AgentCreateRequest")
            .GetProperty("required")
            .EnumerateArray()
            .Select(static element => element.GetString())
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["description", "displayName", "enableSummary", "modelProviderId", "name", "systemPrompt"],
            required
        );
    }

    [Fact]
    public async Task AgentflowNodeDto_PolymorphicVariants_RequireTheirKind()
    {
        var schemas = await GenerateSchemasAsync();
        var discriminator = schemas.GetProperty("AgentflowNodeDto").GetProperty("discriminator");
        Assert.Equal("kind", discriminator.GetProperty("propertyName").GetString());
        var variants = discriminator.GetProperty("mapping").EnumerateObject().ToArray();
        Assert.Equal(12, variants.Length);
        foreach (var variant in variants)
        {
            var schema = schemas.GetProperty(variant.Value.GetString()!.Split('/')[^1]);
            Assert.Contains(schema.GetProperty("required").EnumerateArray(), name => name.GetString() == "kind");
            var kinds = schema.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray();
            Assert.Equal(variant.Name, Assert.Single(kinds).GetString());
        }
    }

    [Fact]
    public async Task AgentflowTraceDto_NodeKind_KeepsItsNumericSchema()
    {
        var schemas = await GenerateSchemasAsync();
        var trace = schemas.GetProperty("AgentflowTraceDto");
        var nodeKind = trace.GetProperty("properties").GetProperty("nodeKind");

        Assert.Equal("#/components/schemas/AgentflowNodeKind", nodeKind.GetProperty("$ref").GetString());
        Assert.Equal("integer", schemas.GetProperty("AgentflowNodeKind").GetProperty("type").GetString());
    }

    [Fact]
    public async Task AgentflowBlockMemberDto_Kind_AllowsOnlyAgentAndWorkflow()
    {
        var schemas = await GenerateSchemasAsync();
        var member = schemas.GetProperty("AgentflowBlockMemberDto");
        var kind = member.GetProperty("properties").GetProperty("kind");

        Assert.Equal("#/components/schemas/AgentflowBlockMemberKind", kind.GetProperty("$ref").GetString());
        var values = schemas.GetProperty("AgentflowBlockMemberKind").GetProperty("enum").EnumerateArray();
        Assert.Equal(["agent", "workflowAsAgent"], values.Select(value => value.GetString()));
    }

    [Fact]
    public async Task AgentflowConditionRuleDto_Field_EnumeratesTheSupportedRuleFields()
    {
        var schemas = await GenerateSchemasAsync();
        var field = schemas.GetProperty("AgentflowConditionRuleDto").GetProperty("properties").GetProperty("field");
        var values = schemas.GetProperty("AgentflowConditionField").GetProperty("enum").EnumerateArray();

        Assert.Equal("#/components/schemas/AgentflowConditionField", field.GetProperty("$ref").GetString());
        Assert.Equal(
            ["contains", "notContains", "equals", "author", "role", "minMessages", "result"],
            values.Select(value => value.GetString())
        );
    }

    [Fact]
    public async Task ExecutionPermissionCapabilities_AgentflowRevision_IsAnOptionalQueryParameter()
    {
        var document = await GenerateDocumentAsync();
        var parameters = document
            .GetProperty("paths")
            .GetProperty("/api/agents/permission-capabilities")
            .GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray();
        var revision = Assert.Single(parameters, item => item.GetProperty("name").GetString() == "agentflowRevision");

        Assert.Equal("query", revision.GetProperty("in").GetString());
        Assert.False(revision.TryGetProperty("required", out var required) && required.GetBoolean());
    }

    private static async Task<JsonElement> GenerateSchemasAsync()
    {
        var document = await GenerateDocumentAsync();
        return document.GetProperty("components").GetProperty("schemas");
    }

    private static async Task<JsonElement> GenerateDocumentAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(AgentsController).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi(options =>
        {
            options.AddSchemaTransformer<AgwOpenApiSchemaTransformer>();
            options.AddSchemaTransformer<A2ACredentialsSchemaTransformer>();
        });
        await using var app = builder.Build();
        app.MapControllers();
        app.MapPost("/notification-answer", (NotificationAnswerRequest request) => request);
        app.MapOpenApi();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: TestContext.Current.CancellationToken
        );
        return document.RootElement.Clone();
    }
}
