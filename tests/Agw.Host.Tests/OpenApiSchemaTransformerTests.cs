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
public sealed class OpenApiSchemaTransformerTests : IClassFixture<OpenApiDocumentFixture>
{
    private readonly OpenApiDocumentFixture _fixture;

    public OpenApiSchemaTransformerTests(OpenApiDocumentFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void NotificationAnswerRequest_TextOnlyAnswer_OptionSchemaAllowsNull()
    {
        var schemas = _fixture.Schemas;
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
    public void AgentUpdateRequest_AllPropertiesNullable_HasNoRequiredProperties()
    {
        var schemas = _fixture.Schemas;

        Assert.False(schemas.GetProperty("AgentUpdateRequest").TryGetProperty("required", out _));
    }

    [Fact]
    public void A2AOriginCredentialsRequest_HeaderValues_AllowNull()
    {
        var schemas = _fixture.Schemas;

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
    public void AgentCreateRequest_RequiredFollowsDeclaredNullability()
    {
        var schemas = _fixture.Schemas;

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
    public void AgentflowNodeDto_PolymorphicVariants_RequireTheirKind()
    {
        var schemas = _fixture.Schemas;
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
    public void AgentflowTraceDto_NodeKind_KeepsItsNumericSchema()
    {
        var schemas = _fixture.Schemas;
        var trace = schemas.GetProperty("AgentflowTraceDto");
        var nodeKind = trace.GetProperty("properties").GetProperty("nodeKind");

        Assert.Equal("#/components/schemas/AgentflowNodeKind", nodeKind.GetProperty("$ref").GetString());
        Assert.Equal("integer", schemas.GetProperty("AgentflowNodeKind").GetProperty("type").GetString());
    }

    [Fact]
    public void AgentflowBlockMemberDto_Kind_AllowsOnlyAgentAndWorkflow()
    {
        var schemas = _fixture.Schemas;
        var member = schemas.GetProperty("AgentflowBlockMemberDto");
        var kind = member.GetProperty("properties").GetProperty("kind");

        Assert.Equal("#/components/schemas/AgentflowBlockMemberKind", kind.GetProperty("$ref").GetString());
        var values = schemas.GetProperty("AgentflowBlockMemberKind").GetProperty("enum").EnumerateArray();
        Assert.Equal(["agent", "workflowAsAgent"], values.Select(value => value.GetString()));
    }

    [Fact]
    public void AgentflowConditionRuleDto_Field_EnumeratesTheSupportedRuleFields()
    {
        var schemas = _fixture.Schemas;
        var field = schemas.GetProperty("AgentflowConditionRuleDto").GetProperty("properties").GetProperty("field");
        var values = schemas.GetProperty("AgentflowConditionField").GetProperty("enum").EnumerateArray();

        Assert.Equal("#/components/schemas/AgentflowConditionField", field.GetProperty("$ref").GetString());
        Assert.Equal(
            ["contains", "notContains", "equals", "author", "role", "minMessages", "result"],
            values.Select(value => value.GetString())
        );
    }

    [Fact]
    public void ExecutionPermissionCapabilities_AgentflowRevision_IsAnOptionalQueryParameter()
    {
        var document = _fixture.Document;
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
}

public sealed class OpenApiDocumentFixture : IAsyncLifetime
{
    public JsonElement Document { get; private set; }

    public JsonElement Schemas => Document.GetProperty("components").GetProperty("schemas");

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
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
        await app.StartAsync(cancellationToken);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/openapi/v1.json", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        Document = document.RootElement.Clone();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
