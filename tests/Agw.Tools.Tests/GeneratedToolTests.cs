using System.Text.Json;
using Agw.Tools.Abstractions.Attributes;
using Agw.Tools.Abstractions.Generated;
using Agw.Tools.Generated;
using Agw.Tools.Generators;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Tools.Tests;

public sealed class GeneratedToolTests
{
    [Fact]
    public async Task GeneratedModule_ContainerAndIgnore_ProducesSchemaAndDirectInvocation()
    {
        // Arrange
        IAgwGeneratedToolModule module = Agw.Generated.Agw.Tools.Tests.AgwToolModule.Instance;
        AgwGeneratedToolDescriptor descriptor = Assert.Single(
            module.Tools,
            static tool => tool.DeclaringType == typeof(GeneratedFixtureTools)
        );
        var services = new ServiceCollection();
        services.AddScoped<GeneratedFixtureDependency>();
        services.AddScoped<GeneratedFixtureTools>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<TestInvocationContext>();
        services.AddScoped<IAgwToolInvocationContext>(static provider =>
            provider.GetRequiredService<TestInvocationContext>()
        );
        services.AddScoped<IAgwToolInvocationContextInitializer>(static provider =>
            provider.GetRequiredService<TestInvocationContext>()
        );
        await using ServiceProvider provider = services.BuildServiceProvider();
        var catalog = new AgwGeneratedToolCatalog(provider.GetRequiredService<IServiceScopeFactory>(), [module]);
        var registry = new ToolRegistryService(
            NullLogger<ToolRegistryService>.Instance,
            provider,
            toolAssemblies: [],
            generatedToolCatalog: catalog,
            generatedToolTypes: [typeof(GeneratedFixtureTools)]
        );
        var arguments = new AIFunctionArguments { ["value"] = JsonSerializer.SerializeToElement("hello") };

        // Act
        object? result = await ((AIFunction)catalog.Create(descriptor, Guid.CreateVersion7())).InvokeAsync(
            arguments,
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal("Echo", descriptor.Name);
        Assert.Equal("Echoes a value.", descriptor.Description);
        Assert.True(descriptor.ExcludeFromList);
        Assert.DoesNotContain(module.Tools, static tool => tool.Name == "Ignored");
        IAgwGeneratedToolBlockRegistration blockRegistration = Assert.Single(module.ToolBlockRegistrations);
        Assert.IsType<GeneratedFixtureToolBlock>(blockRegistration);
        Assert.Equal([typeof(GeneratedFixtureTools)], blockRegistration.ToolTypes);
        AgwGeneratedToolBlockDescriptor block = blockRegistration.CreateDescriptor(catalog);
        Assert.Equal("generated-fixture", block.Name);
        Assert.True(block.ExcludeFromList);
        Assert.Equal(ToolBlockScope.Agent | ToolBlockScope.Project, block.Scopes);
        Assert.Equal(["Echo"], block.MemberToolNames);
        Assert.Equal(["Echo"], Assert.IsType<ToolInfo>(registry.GetTool("generated-fixture")).MemberToolNames);
        Assert.Null(registry.GetTool("Echo"));
        Assert.DoesNotContain(registry.GetListedTools(), static tool => tool.Name == "generated-fixture");
        using JsonDocument schema = JsonDocument.Parse(descriptor.JsonSchema);
        Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("value", out _));
        Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("count", out _));
        Assert.False(schema.RootElement.GetProperty("properties").TryGetProperty("dependency", out _));
        Assert.False(schema.RootElement.GetProperty("properties").TryGetProperty("timeProvider", out _));
        Assert.Equal("hello:2", Assert.IsType<JsonElement>(result).GetString());
    }

    [Fact]
    public void Generator_UnsupportedSignature_ReportsCompileError()
    {
        // Arrange
        const string source = """
            using Agw.Tools.Abstractions;
            using Agw.Tools.Abstractions.Attributes;
            [AgwToolContainer(AgwToolPermission.ReadOnly)]
            public static class InvalidTools
            {
                public static string Read(ref int value) => value.ToString();
            }
            """;
        CSharpCompilation compilation = CSharpCompilation.Create(
            "GeneratorDiagnostics",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AgwToolGenerator());

        // Act
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out _,
            out _,
            TestContext.Current.CancellationToken
        );
        Diagnostic[] diagnostics = driver.GetRunResult().Diagnostics.ToArray();

        // Assert
        Assert.Contains(diagnostics, static diagnostic => diagnostic.Id == "AGWTOOL001");
    }

    [Fact]
    public void Generator_AssemblyName_PreservesNamespaceSegmentsAndSanitizesIdentifiers()
    {
        // Arrange
        const string source = """
            using Agw.Tools.Abstractions;
            using Agw.Tools.Abstractions.Attributes;
            [AgwToolContainer(AgwToolPermission.ReadOnly)]
            public sealed class ValidTools
            {
                public string Read() => "value";
            }
            """;
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Acme.Tools-Next.123.class",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AgwToolGenerator());

        // Act
        GeneratorDriverRunResult result = driver
            .RunGenerators(compilation, TestContext.Current.CancellationToken)
            .GetRunResult();

        // Assert
        GeneratedSourceResult generatedSource = Assert.Single(Assert.Single(result.Results).GeneratedSources);
        Assert.Contains(
            "namespace Agw.Generated.Acme.Tools_Next._123._class;",
            generatedSource.SourceText.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Generator_ToolSetWithoutSkillDependency_GeneratesToolTypes()
    {
        // Arrange
        const string source = """
            using Agw.Tools.Abstractions;
            using Agw.Tools.Abstractions.Attributes;
            using Agw.Tools.Abstractions.Generated;
            [AgwToolContainer(AgwToolPermission.ReadOnly)]
            public sealed class ValidTools
            {
                public string Read() => "value";
            }
            public sealed partial class ToolConsumer : IAgwToolSet<ValidTools>
            {
            }
            """;
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ToolSetWithoutSkillDependency",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AgwToolGenerator());

        // Act
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation outputCompilation,
            out _,
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.DoesNotContain(
            outputCompilation.GetDiagnostics(TestContext.Current.CancellationToken),
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
        );
        Assert.Contains(
            driver.GetRunResult().GeneratedTrees,
            static tree =>
                tree.ToString()
                    .Contains(
                        "public global::System.Collections.Generic.IReadOnlyList<global::System.Type> ToolTypes",
                        StringComparison.Ordinal
                    )
        );
    }

    [Fact]
    public void Generator_ToolSetOwnerIsNotPartial_ReportsCompileError()
    {
        // Arrange
        const string source = """
            using Agw.Tools.Abstractions;
            using Agw.Tools.Abstractions.Attributes;
            using Agw.Tools.Abstractions.Generated;
            [AgwToolContainer(AgwToolPermission.ReadOnly)]
            public static class ValidTools
            {
                public static string Read() => "value";
            }
            public sealed class InvalidBlock : IAgwToolBlockDeclaration, IAgwToolSet<ValidTools>
            {
                public string Name => "invalid";
            }
            """;
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ToolSetDiagnostics",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AgwToolGenerator());

        // Act
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out _,
            out _,
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Contains(driver.GetRunResult().Diagnostics, static diagnostic => diagnostic.Id == "AGWTOOL002");
    }

    [Fact]
    public void Generator_ServiceParameterHasBothAttributes_ReportsCompileError()
    {
        // Arrange
        const string source = """
            using System;
            using Agw.Tools.Abstractions;
            using Agw.Tools.Abstractions.Attributes;
            using Microsoft.AspNetCore.Mvc;
            [AgwToolContainer(AgwToolPermission.ReadOnly)]
            public static class InvalidTools
            {
                public static string Read(
                    [AgwToolService, FromServices] TimeProvider timeProvider) => timeProvider.ToString();
            }
            """;
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ServiceAttributeDiagnostics",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AgwToolGenerator());

        // Act
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out _,
            out _,
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Contains(driver.GetRunResult().Diagnostics, static diagnostic => diagnostic.Id == "AGWTOOL002");
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences()
    {
        string[] frameworkAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(
            Path.PathSeparator
        );
        return frameworkAssemblies
            .Append(typeof(AgwToolAttribute).Assembly.Location)
            .Append(typeof(FromServicesAttribute).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => MetadataReference.CreateFromFile(path));
    }

    private sealed class TestInvocationContext : IAgwToolInvocationContext, IAgwToolInvocationContextInitializer
    {
        public Guid ProjectId { get; private set; }

        public void Initialize(Guid projectId)
        {
            ProjectId = projectId;
        }
    }
}

public sealed class GeneratedFixtureDependency;

[AgwToolContainer(AgwToolPermission.ReadOnly, DefaultCategory = "Tests", AllowInPlanMode = true)]
[ExcludeToolFromList]
public sealed class GeneratedFixtureTools
{
    /// <summary>Echoes a value.</summary>
    public string EchoAsync(
        string value,
        [AgwToolService] GeneratedFixtureDependency dependency,
        [FromServices] TimeProvider timeProvider,
        int count = 2,
        CancellationToken cancellationToken = default
    )
    {
        _ = dependency;
        _ = timeProvider;
        cancellationToken.ThrowIfCancellationRequested();
        return $"{value}:{count}";
    }

    [AgwToolIgnore]
    public string Ignored()
    {
        return "ignored";
    }
}

public sealed partial class GeneratedFixtureToolBlock : IAgwToolBlockDeclaration, IAgwToolSet<GeneratedFixtureTools>
{
    public string Name => "generated-fixture";

    public string DisplayName => "Generated fixture";

    public string Description => "Generated fixture tools.";

    public bool ExcludeFromList => true;
}
