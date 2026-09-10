using System.Text.Json;
using Agw.Shared.Exceptions;
using Agw.Tools.Api.Controllers;
using Agw.Tools.Impl.ToolBlocks.Todo;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Tools.Tests;

public sealed class ToolsControllerTests
{
    [Theory]
    [InlineData("all", "diff", "File")]
    [InlineData("by-category", "diff", "File")]
    [InlineData("detail", "diff", "File")]
    [InlineData("all", "todo", "Tool Blocks")]
    [InlineData("by-category", "todo", "Tool Blocks")]
    [InlineData("detail", "todo", "Tool Blocks")]
    public void Catalog_ReturnsOnlyLiteFields_PreservingSelectionMetadata(string endpoint, string name, string category)
    {
        // Arrange
        using var services = new ServiceCollection().BuildServiceProvider();
        var registry = new ToolRegistryService(
            NullLogger<ToolRegistryService>.Instance,
            services,
            toolBlockRegistry: new ToolBlockRegistry([new TodoToolBlock()])
        );
        var controller = new ToolsController(registry);

        // Act
        var result = endpoint switch
        {
            "all" => controller.GetAllTools(),
            "by-category" => controller.GetToolsByCategory(),
            _ => controller.GetTool(name),
        };
        var data = ReadData(result);
        var item = endpoint switch
        {
            "all" => data.EnumerateArray().Single(item => item.GetProperty("name").GetString() == name),
            "by-category" => data.GetProperty(category)
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == name),
            _ => data,
        };

        // Assert
        Assert.Equal(
            [
                "category",
                "description",
                "displayName",
                "kind",
                "memberToolNames",
                "name",
                "requiredPermission",
                "requiresConfirmation",
                "requiresWorkspace",
                "scopes",
            ],
            item.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
        );
        Assert.Equal(name, item.GetProperty("name").GetString());
        Assert.Equal(category, item.GetProperty("category").GetString());
        Assert.Equal(3, item.GetProperty("scopes").GetInt32());
        Assert.False(item.GetProperty("requiresWorkspace").GetBoolean());
        Assert.False(item.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Equal(name == "todo" ? "toolBlock" : "tool", item.GetProperty("kind").GetString());
        if (name == "todo")
        {
            Assert.Equal(JsonValueKind.Null, item.GetProperty("requiredPermission").ValueKind);
            Assert.Contains(
                "todos_add",
                item.GetProperty("memberToolNames").EnumerateArray().Select(x => x.GetString())
            );
        }
        else
        {
            Assert.Equal("none", item.GetProperty("requiredPermission").GetString());
            Assert.Empty(item.GetProperty("memberToolNames").EnumerateArray());
        }
    }

    [Fact]
    public void GetTool_WriteTool_PreservesApprovalMetadata()
    {
        // Arrange
        using var services = new ServiceCollection().BuildServiceProvider();
        var registry = new ToolRegistryService(NullLogger<ToolRegistryService>.Instance, services);
        var controller = new ToolsController(registry);

        // Act
        var item = ReadData(controller.GetTool("git_clone"));

        // Assert
        Assert.Equal("write", item.GetProperty("requiredPermission").GetString());
        Assert.True(item.GetProperty("requiresConfirmation").GetBoolean());
    }

    [Fact]
    public void GetTool_UnknownName_ReturnsExistingNotFoundEnvelope()
    {
        // Arrange
        using var services = new ServiceCollection().BuildServiceProvider();
        var registry = new ToolRegistryService(NullLogger<ToolRegistryService>.Instance, services);
        var controller = new ToolsController(registry);

        // Act
        var result = controller.GetTool("unknown-tool");

        // Assert
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, Assert.IsAssignableFrom<IApiResult>(result).Code);
    }

    private static JsonElement ReadData(IActionResult result)
    {
        Assert.IsAssignableFrom<IApiResult>(result);
        var data = result.GetType().GetProperty("Data")?.GetValue(result);
        Assert.NotNull(data);
        return JsonSerializer.SerializeToElement(data, data.GetType(), JsonSerializerOptions.Web);
    }
}
