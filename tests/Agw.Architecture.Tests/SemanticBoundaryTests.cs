using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Agw.Architecture.Tests;

public sealed class SemanticBoundaryTests
{
    private static readonly MetadataReference[] References = (
        (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!
    )
        .Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    [Theory]
    [InlineData("using Alias = System.Text.Json.JsonDocument; class C { void M() { Alias.Parse(\"{}\"); } }")]
    [InlineData("class C { void M() { global::System.Text.Json.JsonDocument.Parse(\"{}\"); } }")]
    [InlineData("using static System.Text.Json.JsonDocument; class C { void M() { Parse(\"{}\"); } }")]
    public void JsonGuard_AliasesAndQualifiedReferences_AreDetected(string source) => Assert.True(UsesJson(source));

    [Fact]
    public void JsonGuard_CommentsAndStrings_AreIgnored() =>
        Assert.False(UsesJson("// System.Text.Json.JsonDocument\n class C { string Text = \"System.Text.Json\"; }"));

    [Fact]
    public void Domain_DoesNotDependOnJsonSerialization()
    {
        var violations = Sources()
            .Where(path => path.Replace('\\', '/').Contains("/Domain/", StringComparison.Ordinal))
            .Where(path => UsesJson(File.ReadAllText(path)))
            .ToArray();
        Assert.Empty(violations);
    }

    [Fact]
    public void RuntimeSettings_DoNotDependOnInboundSettingCommand()
    {
        var paths = Sources()
            .Where(path => path.Replace('\\', '/').Contains("/Agw.Agents.Execution/", StringComparison.Ordinal))
            .Where(path =>
                path.Contains("/Agents/Runtime/", StringComparison.Ordinal)
                || path.EndsWith("/Runtimes/ExecutionSettings.cs", StringComparison.Ordinal)
                || path.EndsWith("/Persistence/Durable/DurableExecutionMapper.cs", StringComparison.Ordinal)
            );
        Assert.DoesNotContain(
            paths,
            path =>
                CSharpSyntaxTree
                    .ParseText(File.ReadAllText(path), cancellationToken: TestContext.Current.CancellationToken)
                    .GetRoot(TestContext.Current.CancellationToken)
                    .DescendantTokens()
                    .Any(token => token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == "SettingCommand")
        );
    }

    private static bool UsesJson(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "boundary",
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var model = compilation.GetSemanticModel(tree);
        return tree.GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes()
            .OfType<NameSyntax>()
            .Any(name =>
            {
                var symbol = model.GetSymbolInfo(name).Symbol;
                if (symbol is IAliasSymbol alias)
                    symbol = alias.Target;
                var ns = symbol is INamespaceSymbol namespaceSymbol
                    ? namespaceSymbol.ToDisplayString()
                    : symbol?.ContainingNamespace?.ToDisplayString();
                return ns == "System.Text.Json"
                    || ns?.StartsWith("System.Text.Json.", StringComparison.Ordinal) == true;
            });
    }

    private static IEnumerable<string> Sources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Agw.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Directory
            .EnumerateFiles(Path.Combine(directory.FullName, "src/server"), "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains("/obj/", StringComparison.Ordinal) && !path.Contains("/bin/", StringComparison.Ordinal)
            );
    }
}
