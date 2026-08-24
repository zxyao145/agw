using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Agw.Architecture.Tests;

public sealed partial class BackendArchitectureTests
{
    private static readonly IReadOnlySet<string> GuardedModules = Set(
        "Agw.Agents",
        "Agw.Auth",
        "Agw.Files",
        "Agw.Integrations",
        "Agw.Jobs",
        "Agw.Projects",
        "Agw.Providers",
        "Agw.Setup",
        "Agw.Skills",
        "Agw.Tools"
    );

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedProjectDependencies =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Agw.Standalone.Host"] = Set("Agw.ControlPlane.Host", "Agw.DataPlane.Host", "Agw.Host"),
            ["Agw.ControlPlane.Host"] = Set("Agw.Host"),
            ["Agw.DataPlane.Host"] = Set("Agw.Host"),
            ["Agw.Host"] = Set(
                "Agw.A2A",
                "Agw.Auth",
                "Agw.Infrastructure",
                "Agw.Migrations.Postgres",
                "Agw.Migrations.Sqlite",
                "Agw.Setup"
            ),
            ["Agw.A2A"] = Set("Agw.Agents", "Agw.Projects"),

            ["Agw.Infrastructure"] = Set(
                "Agw.Agents",
                "Agw.Auth",
                "Agw.Integrations",
                "Agw.Jobs",
                "Agw.Projects",
                "Agw.Providers",
                "Agw.Skills"
            ),

            ["Agw.Agents"] = Set(
                "Agw.Auth",
                "Agw.Files",
                "Agw.Integrations",
                "Agw.Providers",
                "Agw.Shared",
                "Agw.Skills",
                "Agw.Tools"
            ),
            ["Agw.Files"] = Set(),

            ["Agw.Integrations"] = Set("Agw.Auth", "Agw.Shared"),
            ["Agw.Jobs"] = Set("Agw.Agents", "Agw.Auth", "Agw.Projects", "Agw.Shared", "Agw.Skills"),
            ["Agw.Projects"] = Set("Agw.Auth", "Agw.Files", "Agw.Shared"),
            ["Agw.Providers"] = Set("Agw.Shared"),
            ["Agw.Skills"] = Set("Agw.Shared"),
            ["Agw.Tools"] = Set("Agw.Auth", "Agw.Files", "Agw.Shared"),

            ["Agw.Auth"] = Set("Agw.Shared"),
            ["Agw.Shared"] = Set("Agw.Data"),
            ["Agw.Data"] = Set(),

            ["Agw.Setup"] = Set("Agw.Auth", "Agw.Infrastructure", "Agw.Shared", "Agw.Skills"),
            ["Agw.Migrations.Postgres"] = Set("Agw.Infrastructure"),
            ["Agw.Migrations.Sqlite"] = Set("Agw.Infrastructure"),
        };

    [Fact]
    public void ProjectReferences_CurrentGraph_MatchesAllowedDependencyMatrix()
    {
        // Arrange
        var serverRoot = GetServerRoot();
        var projectFiles = Directory
            .EnumerateFiles(serverRoot, "*.csproj", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        // Act
        var actualDependencies = projectFiles.ToDictionary(
            static path => Path.GetFileNameWithoutExtension(path),
            ReadProjectReferences,
            StringComparer.Ordinal
        );

        // Assert
        Assert.Equal(
            AllowedProjectDependencies.Keys.OrderBy(static name => name, StringComparer.Ordinal),
            actualDependencies.Keys.OrderBy(static name => name, StringComparer.Ordinal)
        );

        foreach (var (project, allowedDependencies) in AllowedProjectDependencies)
        {
            Assert.Equal(
                allowedDependencies.OrderBy(static name => name, StringComparer.Ordinal),
                actualDependencies[project].OrderBy(static name => name, StringComparer.Ordinal)
            );
        }
    }

    [Fact]
    public void DomainLayer_SourceFiles_DoNotReferenceEfCoreOrAspNetCore()
    {
        // Arrange
        var serverRoot = GetServerRoot();
        var domainFiles = GetSourceFiles(serverRoot)
            .Where(path => HasPathSegment(Path.GetRelativePath(serverRoot, path), "Domain"));

        // Act
        var violations = domainFiles
            .SelectMany(path => FindMatches(serverRoot, path, ForbiddenDomainFrameworkRegex()))
            .OrderBy(static violation => violation, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public void ModuleSource_ReferencingSiblingInternalLayer_HasNoViolations()
    {
        // Arrange
        var serverRoot = GetServerRoot();
        var sourceFiles = GetSourceFiles(serverRoot).ToArray();
        var namespaceOwners = BuildNamespaceOwners(serverRoot, sourceFiles);

        // Act
        var violations = sourceFiles
            .SelectMany(path => FindSiblingInternalLayerReferences(serverRoot, path, namespaceOwners))
            .OrderBy(static violation => violation, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Empty(violations);
    }

    private static HashSet<string> ReadProjectReferences(string projectFile)
    {
        var projectDirectory = Path.GetDirectoryName(projectFile)!;
        var document = XDocument.Load(projectFile);
        return document
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!.Replace('\\', Path.DirectorySeparatorChar))
            .Select(include => Path.GetFullPath(Path.Combine(projectDirectory, include)))
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .Where(static projectName => projectName.StartsWith("Agw.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildNamespaceOwners(
        string serverRoot,
        IReadOnlyList<string> sourceFiles
    )
    {
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var sourceFile in sourceFiles)
        {
            var project = GetOwningProject(serverRoot, sourceFile);
            foreach (var line in File.ReadLines(sourceFile))
            {
                var match = NamespaceDeclarationRegex().Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var declaredNamespace = match.Groups["namespace"].Value;
                if (!owners.TryGetValue(declaredNamespace, out var namespaceProjects))
                {
                    namespaceProjects = new HashSet<string>(StringComparer.Ordinal);
                    owners.Add(declaredNamespace, namespaceProjects);
                }

                namespaceProjects.Add(project);
            }
        }

        return owners.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlySet<string>)pair.Value,
            StringComparer.Ordinal
        );
    }

    private static IEnumerable<string> FindSiblingInternalLayerReferences(
        string serverRoot,
        string sourceFile,
        IReadOnlyDictionary<string, IReadOnlySet<string>> namespaceOwners
    )
    {
        var owningProject = GetOwningProject(serverRoot, sourceFile);
        var relativePath = NormalizePath(Path.GetRelativePath(serverRoot, sourceFile));
        var lineNumber = 0;
        foreach (var line in File.ReadLines(sourceFile))
        {
            lineNumber++;
            var usingMatch = UsingDirectiveRegex().Match(line);
            IEnumerable<string> referencedNamespaces = usingMatch.Success
                ? [usingMatch.Groups["namespace"].Value]
                : QualifiedAgwNameRegex()
                    .Matches(line)
                    .Select(static match => match.Groups["namespace"].Value)
                    .Distinct(StringComparer.Ordinal);

            foreach (var referencedNamespace in referencedNamespaces)
            {
                if (!ContainsInternalLayer(referencedNamespace))
                {
                    continue;
                }

                var referencedProjects = ResolveNamespaceOwners(referencedNamespace, namespaceOwners)
                    .Where(GuardedModules.Contains)
                    .ToHashSet(StringComparer.Ordinal);
                if (referencedProjects.Count == 0 || referencedProjects.Contains(owningProject))
                {
                    continue;
                }

                yield return $"{relativePath}:{lineNumber}: {owningProject} references {referencedNamespace} owned by {string.Join(", ", referencedProjects.OrderBy(static name => name, StringComparer.Ordinal))}";
            }
        }
    }

    private static IReadOnlySet<string> ResolveNamespaceOwners(
        string importedNamespace,
        IReadOnlyDictionary<string, IReadOnlySet<string>> namespaceOwners
    )
    {
        var matchingNamespace = namespaceOwners
            .Keys.Where(declared =>
                string.Equals(importedNamespace, declared, StringComparison.Ordinal)
                || importedNamespace.StartsWith($"{declared}.", StringComparison.Ordinal)
            )
            .OrderByDescending(static declared => declared.Length)
            .FirstOrDefault();

        return matchingNamespace == null ? Set() : namespaceOwners[matchingNamespace];
    }

    private static IEnumerable<string> FindMatches(string serverRoot, string sourceFile, Regex pattern)
    {
        var relativePath = NormalizePath(Path.GetRelativePath(serverRoot, sourceFile));
        var lineNumber = 0;
        foreach (var line in File.ReadLines(sourceFile))
        {
            lineNumber++;
            foreach (Match match in pattern.Matches(line))
            {
                yield return $"{relativePath}:{lineNumber}: {match.Value}";
            }
        }
    }

    private static IEnumerable<string> GetSourceFiles(string serverRoot) =>
        Directory
            .EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasPathSegment(Path.GetRelativePath(serverRoot, path), "bin"))
            .Where(path => !HasPathSegment(Path.GetRelativePath(serverRoot, path), "obj"));

    private static string GetOwningProject(string serverRoot, string sourceFile)
    {
        var relativePath = Path.GetRelativePath(serverRoot, sourceFile);
        var projectDirectory = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return projectDirectory;
    }

    private static bool ContainsInternalLayer(string namespaceName) =>
        namespaceName.Split('.').Any(static segment => segment is "Application" or "Domain" or "Infrastructure");

    private static bool HasPathSegment(string path, string expectedSegment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, expectedSegment, StringComparison.OrdinalIgnoreCase));

    private static string GetServerRoot() => Path.Combine(FindRepositoryRoot(), "src", "server");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Agw.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static string NormalizePath(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    [GeneratedRegex(@"\bMicrosoft\.(?:EntityFrameworkCore|AspNetCore)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenDomainFrameworkRegex();

    [GeneratedRegex(@"^\s*namespace\s+(?<namespace>[A-Za-z_][A-Za-z0-9_.]*)\s*[;{]", RegexOptions.CultureInvariant)]
    private static partial Regex NamespaceDeclarationRegex();

    [GeneratedRegex(
        @"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:(?:[A-Za-z_][A-Za-z0-9_]*)\s*=\s*)?(?<namespace>Agw(?:\.[A-Za-z_][A-Za-z0-9_]*)+)\s*;",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex UsingDirectiveRegex();

    [GeneratedRegex(@"\b(?:global::)?(?<namespace>Agw(?:\.[A-Za-z_][A-Za-z0-9_]*)+)", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedAgwNameRegex();
}
