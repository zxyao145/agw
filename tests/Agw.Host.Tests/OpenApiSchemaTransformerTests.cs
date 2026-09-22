using System.Text.Json;
using Agw.Agents.Definitions.Controllers;
using Agw.Host.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Host.Tests;

/// <summary>
/// <para>通用 schema 规则按 DTO 声明的可空性决定 required，Host 不需要知道任何模块 DTO 的字段。</para>
/// <para>The generic schema rule derives required from DTO nullability; Host knows no module DTO fields.</para>
/// </summary>
public sealed class OpenApiSchemaTransformerTests
{
    [Fact]
    public async Task AgentUpdateRequest_AllPropertiesNullable_HasNoRequiredProperties()
    {
        var schemas = await GenerateSchemasAsync();

        Assert.False(schemas.GetProperty("AgentUpdateRequest").TryGetProperty("required", out _));
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

    private static async Task<JsonElement> GenerateSchemasAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(AgentsController).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi(options => options.AddSchemaTransformer<AgwOpenApiSchemaTransformer>());
        await using var app = builder.Build();
        app.MapControllers();
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
        return document.RootElement.GetProperty("components").GetProperty("schemas").Clone();
    }
}
