using System.Text.Json;

using Agw.Domain.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Tools.Tests;

public class ToolRegistryServiceTests
{
    [Fact]
    public void CreateAIFunction_ForRegisteredParameterObjectTool_ExposesFlattenedParameterSchema()
    {
        var tool = CreateReadFileFunction();
        var schemaText = tool.JsonSchema.GetRawText();

        Assert.Contains("filePath", schemaText);
        Assert.DoesNotContain("toolParams", schemaText);
    }

    [Fact]
    public async Task InvokeAsync_ForRegisteredParameterObjectTool_AcceptsFlattenedArguments()
    {
        var filePath = Path.GetTempFileName();
        await File.WriteAllTextAsync(filePath, "line 1\nline 2", TestContext.Current.CancellationToken);

        try
        {
            var tool = CreateReadFileFunction();
            var arguments = new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["limit"] = 1
            });

            var result = await tool.InvokeAsync(arguments, TestContext.Current.CancellationToken);
            var resultJson = Assert.IsType<JsonElement>(result);

            Assert.Equal(filePath, resultJson.GetProperty("filePath").GetString());
            Assert.Equal("line 1", resultJson.GetProperty("content").GetString());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static AIFunction CreateReadFileFunction()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var registry = new ToolRegistryService(NullLogger<ToolRegistryService>.Instance, services);

        return Assert.IsAssignableFrom<AIFunction>(registry.CreateAIFunction("read_file"));
    }
}
