using System.Text.Json;

using Agw.Integrations.Application.Capabilities;
using Agw.Integrations.Application.Management;
using Agw.Integrations.Contracts.Management;
using Agw.Integrations.Contracts.OAuth;
using Agw.Integrations.Controllers;
using Agw.Integrations.Infrastructure.Plugins;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Integrations.Tests;

public class IntegrationManagementControllerTests
{
    [Fact]
    public void Controllers_ExposeExpectedRouteSurfaceWithoutPathIdentifiers()
    {
        Assert.Equal("api/integrations/plugins", RouteOf<PluginsController>());
        AssertMethod<PluginsController>(nameof(PluginsController.List), typeof(HttpGetAttribute), null);

        Assert.Equal("api/integrations/plugin-installations", RouteOf<PluginInstallationsController>());
        AssertMethod<PluginInstallationsController>(
            nameof(PluginInstallationsController.UpsertAsync),
            typeof(HttpPutAttribute),
            null);

        Assert.Equal("api/integrations/connections", RouteOf<ConnectionsController>());
        AssertMethod<ConnectionsController>(nameof(ConnectionsController.ListAsync), typeof(HttpGetAttribute), null);
        AssertMethod<ConnectionsController>(nameof(ConnectionsController.CreateAsync), typeof(HttpPostAttribute), null);
        AssertMethod<ConnectionsController>(nameof(ConnectionsController.UpdateAsync), typeof(HttpPutAttribute), null);
        AssertMethod<ConnectionsController>(nameof(ConnectionsController.DeleteAsync), typeof(HttpDeleteAttribute), null);
        AssertMethod<ConnectionsController>(
            nameof(ConnectionsController.ValidateAsync),
            typeof(HttpPostAttribute),
            "validate");

        Assert.Equal("api/integrations/oauth", RouteOf<OAuthController>());
        AssertMethod<OAuthController>(
            nameof(OAuthController.GetCallbackInfo),
            typeof(HttpGetAttribute),
            "callback-info");
        AssertMethod<OAuthController>(
            nameof(OAuthController.AuthorizeStartAsync),
            typeof(HttpPostAttribute),
            "authorize-start");
        AssertMethod<OAuthController>(
            nameof(OAuthController.CallbackAsync),
            typeof(HttpGetAttribute),
            "callback");
        AssertMethod<OAuthController>(
            nameof(OAuthController.DesktopComplete),
            typeof(HttpGetAttribute),
            "desktop-complete");
        AssertMethod<OAuthController>(
            nameof(OAuthController.RefreshAsync),
            typeof(HttpPostAttribute),
            "refresh");

        var deleteId = typeof(ConnectionsController)
            .GetMethod(nameof(ConnectionsController.DeleteAsync))!
            .GetParameters()
            .Single(parameter => parameter.Name == "id");
        Assert.NotNull(deleteId.GetCustomAttributes(typeof(FromQueryAttribute), inherit: true).SingleOrDefault());
    }

    [Fact]
    public async Task PluginsController_List_ReturnsBensResultsEnvelope()
    {
        var controller = new PluginsController(
            new PluginCatalogAppService(
                new BuiltInPluginCatalog(),
                new PluginSkillMetadataReader(new AppContextPluginContentRootProvider())));

        var result = await controller.List(TestContext.Current.CancellationToken);

        Assert.IsAssignableFrom<IActionResult>(result);
        Assert.StartsWith("Bens.Results", result.GetType().Namespace, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiEnums_WhenSerialized_UseStableStringValues()
    {
        Assert.Equal("\"Ready\"", JsonSerializer.Serialize(ConnectionStatusResponse.Ready));
        Assert.Equal("\"Set\"", JsonSerializer.Serialize(SecretUpdateAction.Set));
        Assert.Equal("\"Mcp\"", JsonSerializer.Serialize(CapabilitySourceKindResponse.Mcp));
        Assert.Equal("\"OAuth2\"", JsonSerializer.Serialize(AuthSchemeTypeResponse.OAuth2));
        Assert.Equal("\"AkSk\"", JsonSerializer.Serialize(AuthSchemeTypeResponse.AkSk));
        Assert.Equal("\"Desktop\"", JsonSerializer.Serialize(OAuthCompletionTarget.Desktop));
    }

    private static string RouteOf<TController>()
    {
        return typeof(TController).GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .Single()
            .Template;
    }

    private static void AssertMethod<TController>(
        string methodName,
        Type attributeType,
        string? expectedTemplate)
    {
        var method = typeof(TController).GetMethod(methodName);
        Assert.NotNull(method);
        var attribute = Assert.Single(method!.GetCustomAttributes(attributeType, inherit: true));
        var template = attributeType.GetProperty("Template")?.GetValue(attribute) as string;
        Assert.Equal(expectedTemplate, template);
        Assert.DoesNotContain("{", template ?? string.Empty, StringComparison.Ordinal);
    }
}
