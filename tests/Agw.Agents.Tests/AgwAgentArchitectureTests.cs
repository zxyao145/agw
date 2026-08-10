namespace Agw.Agents.Tests;

public sealed class AgwAgentArchitectureTests
{
    [Fact]
    public void AgentRuntimeSource_DoesNotUseHarnessAgentWrappers()
    {
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var sourceRoot = Path.Combine(repositoryRoot, "src", "server", "Agw.Agents");
        var forbiddenTerms = new[]
        {
            string.Concat("Harness", "Agent"),
            string.Concat("Harness", "AgentOptions"),
            string.Concat("As", "Harness", "Agent")
        };

        var violations = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .SelectMany(path => forbiddenTerms
                .Where(term => File.ReadAllText(path).Contains(term, StringComparison.Ordinal))
                .Select(term => $"{Path.GetRelativePath(repositoryRoot, path)}: {term}"))
            .ToArray();

        Assert.Empty(violations);

        var projectFile = File.ReadAllText(Path.Combine(sourceRoot, "Agw.Agents.csproj"));
        Assert.DoesNotContain(
            string.Concat("Microsoft.Agents.AI.", "Harness"),
            projectFile,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionSource_DoesNotReferenceRemovedBuildingBlockModules()
    {
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var forbiddenTerms = new[]
        {
            string.Concat("Agw.", "BuildingBlocks"),
            string.Concat("BuildingBlock", "Definition"),
            string.Concat("BuildingBlock", "Registry"),
            string.Concat("@agw/", "building-blocks")
        };
        var sourceFiles = Directory
            .EnumerateFiles(
                Path.Combine(repositoryRoot, "src"),
                "*",
                SearchOption.AllDirectories)
            .Where(path =>
                (path.EndsWith(".cs", StringComparison.Ordinal) ||
                 path.EndsWith(".csproj", StringComparison.Ordinal) ||
                 path.EndsWith(".ts", StringComparison.Ordinal) ||
                 path.EndsWith(".tsx", StringComparison.Ordinal) ||
                 path.EndsWith("package.json", StringComparison.Ordinal)) &&
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) &&
                !path.EndsWith("openapi.d.ts", StringComparison.Ordinal) &&
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) &&
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal));

        var violations = sourceFiles
            .SelectMany(path => forbiddenTerms
                .Where(term => File.ReadAllText(path).Contains(term, StringComparison.Ordinal))
                .Select(term => $"{Path.GetRelativePath(repositoryRoot, path)}: {term}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void PersistedCapabilities_UseOnlyTheUnifiedToolsColumn()
    {
        Assert.Null(typeof(Agw.Shared.Data.Entities.Agents.Agent).GetProperty("ToolBlocks"));
        Assert.Null(typeof(Agw.Shared.Data.Entities.Projects.Project).GetProperty("ToolBlocks"));

        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var migrationFiles = Directory
            .EnumerateFiles(
                Path.Combine(repositoryRoot, "src", "server", "Agw.Infrastructure", "Migrations"),
                "*.cs",
                SearchOption.TopDirectoryOnly)
            .Where(path =>
                Path.GetFileName(path).StartsWith("20260729", StringComparison.Ordinal) ||
                path.EndsWith("AgwDbContextModelSnapshot.cs", StringComparison.Ordinal));
        var violations = migrationFiles
            .Where(path => File.ReadAllText(path).Contains("tool_blocks", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.Empty(violations);
    }

    private static string FindRepositoryRoot(string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Agw.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new InvalidOperationException("Could not locate the repository root.");
    }
}
