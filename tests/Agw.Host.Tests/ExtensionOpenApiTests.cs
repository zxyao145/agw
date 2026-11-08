using System.Text.Json;
using Agw.ControlPlane.Host;
using Agw.Host.OpenApi;
using Bens.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Host.Tests;

public sealed class ExtensionOpenApiTests
{
    [Fact]
    public async Task Document_ExtensionManagementAndSelections_ExposeStableModels()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var controllers = builder.Services.AddControllers();
        new ControlPlaneHostModule().AddApplicationParts(controllers.PartManager);
        builder.Services.AddApiResult();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi(options =>
        {
            options.AddSchemaTransformer<AgwOpenApiSchemaTransformer>();
        });
        await using var app = builder.Build();
        app.MapControllers();
        app.MapOpenApi();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var artifacts = Path.Combine(AppContext.BaseDirectory, "test-artifacts");
        Directory.CreateDirectory(artifacts);
        await File.WriteAllTextAsync(
            Path.Combine(artifacts, "extension-openapi.json"),
            document.RootElement.GetRawText(),
            TestContext.Current.CancellationToken
        );
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/extensions", out _));
        Assert.True(paths.TryGetProperty("/api/extensions/enable", out _));
        Assert.True(paths.TryGetProperty("/api/extensions/disable", out _));
        Assert.True(paths.TryGetProperty("/api/extensions/catalog", out _));
        Assert.True(paths.TryGetProperty("/api/extensions/package", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var enablement = schemas.GetProperty("ExtensionEnablementRequest");
        Assert.Equal("extensionId", Assert.Single(enablement.GetProperty("required").EnumerateArray()).GetString());
        Assert.Equal("extensionId", Assert.Single(enablement.GetProperty("properties").EnumerateObject()).Name);
        Assert.True(
            schemas.GetProperty("ServerInfoResponse").GetProperty("properties").TryGetProperty("serverId", out _)
        );
        var tool = schemas.GetProperty("ToolDefinitionExtensionToolDefinition").GetProperty("properties");
        Assert.True(tool.TryGetProperty("extensionId", out _));
        Assert.True(tool.TryGetProperty("toolId", out _));
    }
}
