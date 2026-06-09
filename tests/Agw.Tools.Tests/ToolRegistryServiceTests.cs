using System.Text.Json;

using Agw.Domain.Services;
using Agw.Files.Application.Storage.Local;
using Agw.Shared.Contracts.Storage;

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
        var tempDir = Path.Combine(Path.GetTempPath(), $"agw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "line 1\nline 2", TestContext.Current.CancellationToken);

        try
        {
            var tool = CreateReadFileFunction(tempDir);
            var arguments = new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["filePath"] = "test.txt",
                ["limit"] = 1
            });

            var result = await tool.InvokeAsync(arguments, TestContext.Current.CancellationToken);
            var resultJson = Assert.IsType<JsonElement>(result);

            Assert.Equal("test.txt", resultJson.GetProperty("filePath").GetString());
            Assert.Equal("line 1", resultJson.GetProperty("content").GetString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static AIFunction CreateReadFileFunction(string? tempDir = null)
    {
        var testDir = tempDir ?? Path.Combine(Path.GetTempPath(), $"agw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);

        var services = new ServiceCollection();
        services.AddSingleton<IAgwFileSystemResolver>(_ => new TestFileSystemResolver(testDir));
        using var sp = services.BuildServiceProvider();

        var registry = new ToolRegistryService(NullLogger<ToolRegistryService>.Instance, sp);
        return Assert.IsAssignableFrom<AIFunction>(registry.CreateAIFunction("read_file"));
    }

    private sealed class TestFileSystemResolver(string rootPath) : IAgwFileSystemResolver
    {
        public Task<IAgwFileSystem> ResolveAsync(Guid projectId, CancellationToken ct)
        {
            return Task.FromResult<IAgwFileSystem>(new LocalFileSystem(rootPath));
        }
    }
}
