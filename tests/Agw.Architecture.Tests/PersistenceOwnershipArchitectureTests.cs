using System.Text.RegularExpressions;
using Xunit;

namespace Agw.Architecture.Tests;

public sealed partial class BackendArchitectureTests
{
    [Fact]
    public void PersistenceOwnership_ContainsEveryTableEntityExactlyOnce()
    {
        // Arrange
        var entitiesRoot = Path.Combine(GetServerRoot(), "Agw.Data", "Entities");

        // Act
        var tableEntities = Directory
            .EnumerateFiles(entitiesRoot, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Where(source => source.Contains("[Table(", StringComparison.Ordinal))
            .Select(source => TableEntityClassRegex().Match(source))
            .Where(static match => match.Success)
            .Select(match => match.Groups["entity"].Value)
            .OrderBy(static entity => entity, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Equal(EntityOwners.Keys.OrderBy(static entity => entity, StringComparer.Ordinal), tableEntities);
    }

    [Fact]
    public void ModuleDbContextInterfaces_DbSetsMatchPersistenceOwnership()
    {
        // Arrange
        var serverRoot = GetServerRoot();
        var sourceFiles = GetSourceFiles(serverRoot).ToArray();

        foreach (var (contextType, owner) in ModuleDbContextOwners)
        {
            // Act
            var contextSource = Assert.Single(
                sourceFiles,
                path => File.ReadAllText(path).Contains($"interface {contextType}", StringComparison.Ordinal)
            );
            var actualEntities = ModuleDbSetRegex()
                .Matches(File.ReadAllText(contextSource))
                .Select(match => match.Groups["entity"].Value)
                .OrderBy(static entity => entity, StringComparer.Ordinal)
                .ToArray();
            var expectedEntities = EntityOwners
                .Where(pair => pair.Value == owner)
                .Select(static pair => pair.Key)
                .OrderBy(static entity => entity, StringComparer.Ordinal)
                .ToArray();

            // Assert
            Assert.Equal(owner, GetOwningProject(serverRoot, contextSource));
            Assert.Equal(expectedEntities, actualEntities);
        }
    }

    [Fact]
    public void ModuleSource_DoesNotReferenceForeignModuleDbContextInterfaces()
    {
        // Arrange
        var serverRoot = GetServerRoot();

        // Act
        var violations = GetSourceFiles(serverRoot)
            .SelectMany(path =>
            {
                var project = GetOwningProject(serverRoot, path);
                return ModuleDbContextReferenceRegex()
                    .Matches(File.ReadAllText(path))
                    .Select(match => match.Groups["context"].Value)
                    .Distinct(StringComparer.Ordinal)
                    .Where(context => project != "Agw.Infrastructure" && ModuleDbContextOwners[context] != project)
                    .Select(context =>
                        $"{NormalizePath(Path.GetRelativePath(serverRoot, path))}: {project} references {context} owned by {ModuleDbContextOwners[context]}"
                    );
            })
            .OrderBy(static violation => violation, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public void OnlyAgwDbContext_ImplementsMultipleModuleDbContextInterfaces()
    {
        // Arrange
        var serverRoot = GetServerRoot();

        // Act
        var implementations = GetSourceFiles(serverRoot)
            .Select(path => new { Path = path, Match = ClassBaseListRegex().Match(File.ReadAllText(path)) })
            .Where(static item => item.Match.Success)
            .Select(item => new
            {
                item.Path,
                Count = ModuleDbContextReferenceRegex()
                    .Matches(item.Match.Groups["bases"].Value)
                    .Select(match => match.Groups["context"].Value)
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
            })
            .Where(static item => item.Count > 1)
            .Select(item => NormalizePath(Path.GetRelativePath(serverRoot, item.Path)))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Equal(["Agw.Infrastructure/Data/AgwDbContext.cs"], implementations);
    }

    [Fact]
    public void ContractsProjects_DoNotReferencePersistenceInterfacesOrTypes()
    {
        // Arrange
        var serverRoot = GetServerRoot();
        var contractProjects = Directory
            .EnumerateDirectories(serverRoot, "*.Contracts", SearchOption.TopDirectoryOnly)
            .ToArray();

        // Act
        var violations = contractProjects
            .SelectMany(project => GetSourceFiles(project))
            .SelectMany(path => FindMatches(serverRoot, path, ForbiddenContractsPersistenceRegex()))
            .OrderBy(static violation => violation, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public void ModuleSource_RawPersistenceAccessDoesNotExceedBaseline()
    {
        // Arrange
        var serverRoot = GetServerRoot();

        // Act
        var actualCounts = RawPersistenceAccessBaselines.Keys.ToDictionary(
            project => project,
            project =>
                GetSourceFiles(Path.Combine(serverRoot, project))
                    .Select(path => RawPersistenceAccessRegex().Matches(File.ReadAllText(path)).Count)
                    .Sum(),
            StringComparer.Ordinal
        );

        // Assert
        foreach (var (project, baseline) in RawPersistenceAccessBaselines)
        {
            Assert.True(
                actualCounts[project] <= baseline,
                $"{project} raw persistence access grew from {baseline} to {actualCounts[project]}."
            );
        }
    }

    private static readonly IReadOnlyDictionary<string, string> ModuleDbContextOwners = new Dictionary<string, string>(
        StringComparer.Ordinal
    )
    {
        ["IAgentsDbContext"] = "Agw.Agents",
        ["IAuthDbContext"] = "Agw.Auth",
        ["IIntegrationsDbContext"] = "Agw.Integrations",
        ["IJobsDbContext"] = "Agw.Jobs",
        ["IProjectsDbContext"] = "Agw.Projects",
        ["IProvidersDbContext"] = "Agw.Providers",
        ["ISkillsDbContext"] = "Agw.Skills",
        ["IToolsDbContext"] = "Agw.Tools",
    };

    private static readonly IReadOnlyDictionary<string, int> RawPersistenceAccessBaselines = new Dictionary<
        string,
        int
    >(StringComparer.Ordinal)
    {
        ["Agw.Agents"] = 77,
        ["Agw.Auth"] = 0,
        ["Agw.Integrations"] = 28,
        ["Agw.Jobs"] = 12,
        ["Agw.Projects"] = 60,
        ["Agw.Providers"] = 10,
        ["Agw.Setup"] = 6,
        ["Agw.Skills"] = 8,
        ["Agw.Tools"] = 9,
    };

    [GeneratedRegex(@"\[Table\([^\]]+\)\][\s\S]*?\bclass\s+(?<entity>[A-Za-z_][A-Za-z0-9_]*)\b")]
    private static partial Regex TableEntityClassRegex();

    [GeneratedRegex(@"\bDbSet\s*<\s*(?<entity>[A-Za-z_][A-Za-z0-9_]*)\s*>")]
    private static partial Regex ModuleDbSetRegex();

    [GeneratedRegex(@"\b(?<context>I(?:Agents|Auth|Integrations|Jobs|Projects|Providers|Skills|Tools)DbContext)\b")]
    private static partial Regex ModuleDbContextReferenceRegex();

    [GeneratedRegex(@"\bclass\s+[A-Za-z_][A-Za-z0-9_]*\s*:\s*(?<bases>[^\{]+)\{")]
    private static partial Regex ClassBaseListRegex();

    [GeneratedRegex(
        @"\b(?:Microsoft\.EntityFrameworkCore|Agw\.Shared\.Data|IModuleDbContext|I(?:Agents|Auth|Integrations|Jobs|Projects|Providers|Skills|Tools)DbContext)\b"
    )]
    private static partial Regex ForbiddenContractsPersistenceRegex();

    [GeneratedRegex(@"\b(?:AgwDbContext|DbContext)\b|\bIRepository\s*<")]
    private static partial Regex RawPersistenceAccessRegex();
}
