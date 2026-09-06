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

    private static readonly IReadOnlySet<string> AllowedRootNamespaceFacadeFiles = Set(
        "Agw.Agents/DependencyInjection.cs",
        "Agw.Files/DependencyInjection.cs",
        "Agw.Jobs/DependencyInjection.cs",
        "Agw.Projects/DependencyInjection.cs",
        "Agw.Providers/DependencyInjection.cs",
        "Agw.Skills/DependencyInjection.cs",
        "Agw.Tools/Extensions/DependencyInjection.cs",
        "Agw.Tools/ToolRegistryService.cs",
        "Agw.Tools/ToolValueResolution.cs"
    );

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedProjectDependencies =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Agw.Standalone.Host"] = Set("Agw.ControlPlane.Host", "Agw.DataPlane.Host", "Agw.Host"),
            ["Agw.ControlPlane.Host"] = Set("Agw.Host"),
            ["Agw.DataPlane.Host"] = Set("Agw.Host"),
            ["Agw.Host"] = Set(
                "Agw.A2A",
                "Agw.Agents.Contracts",
                "Agw.Auth",
                "Agw.Infrastructure",
                "Agw.Jobs.Contracts",
                "Agw.Migrations.Postgres",
                "Agw.Migrations.Sqlite",
                "Agw.Projects.Contracts",
                "Agw.Setup"
            ),
            ["Agw.A2A"] = Set("Agw.Agents.Contracts", "Agw.Auth", "Agw.Projects.Contracts", "Agw.Shared"),

            ["Agw.Infrastructure"] = Set(
                "Agw.Agents",
                "Agw.Auth",
                "Agw.Data",
                "Agw.Integrations",
                "Agw.Jobs",
                "Agw.Projects",
                "Agw.Providers",
                "Agw.Skills",
                "Agw.Tools"
            ),

            ["Agw.Agents"] = Set(
                "Agw.Agents.Contracts",
                "Agw.Auth",
                "Agw.Data",
                "Agw.Files",
                "Agw.Integrations",
                "Agw.Projects.Contracts",
                "Agw.Providers",
                "Agw.Shared",
                "Agw.Skills",
                "Agw.Tools"
            ),
            ["Agw.Agents.Contracts"] = Set("Agw.Projects.Contracts", "Agw.Shared"),
            ["Agw.Files"] = Set(),

            ["Agw.Integrations"] = Set("Agw.Auth", "Agw.Data", "Agw.Projects.Contracts", "Agw.Shared"),
            ["Agw.Jobs"] = Set(
                "Agw.Agents.Contracts",
                "Agw.Auth",
                "Agw.Data",
                "Agw.Jobs.Contracts",
                "Agw.Projects.Contracts",
                "Agw.Shared",
                "Agw.Skills"
            ),
            ["Agw.Jobs.Contracts"] = Set(),
            ["Agw.Projects"] = Set(
                "Agw.Agents.Contracts",
                "Agw.Auth",
                "Agw.Data",
                "Agw.Files",
                "Agw.Integrations",
                "Agw.Projects.Contracts",
                "Agw.Skills",
                "Agw.Shared"
            ),
            ["Agw.Projects.Contracts"] = Set("Agw.Shared"),
            ["Agw.Providers"] = Set("Agw.Agents.Contracts", "Agw.Data", "Agw.Shared"),
            ["Agw.Skills"] = Set("Agw.Agents.Contracts", "Agw.Data", "Agw.Shared"),
            ["Agw.Tools"] = Set("Agw.Agents.Contracts", "Agw.Auth", "Agw.Data", "Agw.Files", "Agw.Shared"),

            ["Agw.Auth"] = Set("Agw.Data", "Agw.Shared"),
            ["Agw.Shared"] = Set(),
            ["Agw.Data"] = Set("Agw.Agents.Contracts", "Agw.Shared"),

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
    public void GuardedModuleRootNamespaces_ContainOnlyExplicitFacades()
    {
        // Arrange
        var serverRoot = GetServerRoot();

        // Act
        var actualFacadeFiles = GetSourceFiles(serverRoot)
            .Where(path => GuardedModules.Contains(GetOwningProject(serverRoot, path)))
            .Where(path => DeclaresNamespace(path, GetOwningProject(serverRoot, path)))
            .Select(path => NormalizePath(Path.GetRelativePath(serverRoot, path)))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Equal(
            AllowedRootNamespaceFacadeFiles.OrderBy(static path => path, StringComparer.Ordinal),
            actualFacadeFiles
        );
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

    [Fact]
    public void ContractsProjects_ProjectReferences_DoNotDependOnImplementations()
    {
        // Arrange
        var serverRoot = GetServerRoot();
        var contractProjects = Directory.EnumerateFiles(serverRoot, "*.Contracts.csproj", SearchOption.AllDirectories);

        // Act
        var violations = contractProjects
            .SelectMany(project =>
                ReadProjectReferences(project)
                    .Where(dependency =>
                        dependency != "Agw.Shared" && !dependency.EndsWith(".Contracts", StringComparison.Ordinal)
                    )
                    .Select(dependency => $"{Path.GetFileNameWithoutExtension(project)} -> {dependency}")
            )
            .OrderBy(static violation => violation, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public void ModuleSource_ForeignPersistenceAccessesMatchLegacyAllowlist()
    {
        // Arrange
        var serverRoot = GetServerRoot();

        // Act
        var actualCounts = GetSourceFiles(serverRoot)
            .SelectMany(path => FindForeignPersistenceAccesses(serverRoot, path))
            .GroupBy(static access => access.InventoryKey, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

        // Assert
        Assert.Equal(
            AllowedLegacyForeignPersistenceAccessCounts.Keys.OrderBy(static access => access, StringComparer.Ordinal),
            actualCounts.Keys.OrderBy(static access => access, StringComparer.Ordinal)
        );
        foreach (var (access, expectedCount) in AllowedLegacyForeignPersistenceAccessCounts)
        {
            Assert.Equal(expectedCount, actualCounts[access]);
        }
    }

    [Fact]
    public void ModuleSource_CrossModuleConcreteServicesOrOwnedRepositories_HasNoViolations()
    {
        // Arrange
        var serverRoot = GetServerRoot();
        var sourceFiles = GetSourceFiles(serverRoot).ToArray();
        var implementationOwners = BuildImplementationTypeOwners(serverRoot, sourceFiles);

        // Act
        var violations = sourceFiles
            .SelectMany(path => FindConcreteServiceAndRepositoryViolations(serverRoot, path, implementationOwners))
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

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildImplementationTypeOwners(
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
                var match = ImplementationTypeDeclarationRegex().Match(line);
                if (!match.Success)
                {
                    continue;
                }
                var type = match.Groups["type"].Value;
                if (!owners.TryGetValue(type, out var projects))
                {
                    projects = new HashSet<string>(StringComparer.Ordinal);
                    owners.Add(type, projects);
                }
                projects.Add(project);
            }
        }
        return owners.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlySet<string>)pair.Value,
            StringComparer.Ordinal
        );
    }

    private static IEnumerable<string> FindConcreteServiceAndRepositoryViolations(
        string serverRoot,
        string sourceFile,
        IReadOnlyDictionary<string, IReadOnlySet<string>> implementationOwners
    )
    {
        var owningProject = GetOwningProject(serverRoot, sourceFile);
        if (owningProject == "Agw.Infrastructure")
        {
            yield break;
        }

        var relativePath = NormalizePath(Path.GetRelativePath(serverRoot, sourceFile));
        foreach (var access in FindForeignPersistenceAccesses(serverRoot, sourceFile))
        {
            if (!AllowedLegacyForeignPersistenceAccessCounts.ContainsKey(access.InventoryKey))
            {
                yield return access.Violation;
            }
        }

        var lineNumber = 0;
        foreach (var line in File.ReadLines(sourceFile))
        {
            lineNumber++;
            foreach (Match match in ImplementationTypeReferenceRegex().Matches(line))
            {
                var type = match.Groups["type"].Value;
                if (
                    implementationOwners.TryGetValue(type, out var owners)
                    && !owners.Contains(owningProject)
                    && owners.Any(owner => owner is "Agw.Agents" or "Agw.Projects" or "Agw.Jobs")
                )
                {
                    yield return $"{relativePath}:{lineNumber}: {owningProject} references concrete {type}";
                }
            }

            foreach (Match match in LegacyCrossModuleServiceReferenceRegex().Matches(line))
            {
                var type = match.Groups["type"].Value;
                var owner = LegacyCrossModuleServiceOwners.GetValueOrDefault(type);
                if (owner != null && owningProject != owner && owningProject != "Agw.Shared")
                {
                    yield return $"{relativePath}:{lineNumber}: {owningProject} references legacy {type} owned by {owner}";
                }
            }
        }
    }

    private static IEnumerable<(string InventoryKey, string Violation)> FindForeignPersistenceAccesses(
        string serverRoot,
        string sourceFile
    )
    {
        var owningProject = GetOwningProject(serverRoot, sourceFile);
        if (owningProject == "Agw.Infrastructure")
        {
            yield break;
        }

        var relativePath = NormalizePath(Path.GetRelativePath(serverRoot, sourceFile));
        var lineNumber = 0;
        foreach (var line in File.ReadLines(sourceFile))
        {
            lineNumber++;
            foreach (Match match in OwnedRepositoryRegex().Matches(line))
            {
                var entity = match.Groups["entity"].Value;
                var owner = EntityOwners.GetValueOrDefault(entity);
                if (owner != null && owner != owningProject)
                {
                    yield return (
                        $"{relativePath}:IRepository<{entity}>",
                        $"{relativePath}:{lineNumber}: {owningProject} references IRepository<{entity}> owned by {owner}"
                    );
                }
            }

            foreach (Match match in OwnedDbSetAccessRegex().Matches(line))
            {
                var entity = match.Groups["entity"].Value;
                var owner = EntityOwners.GetValueOrDefault(entity);
                if (owner != null && owner != owningProject)
                {
                    yield return (
                        $"{relativePath}:DbSet<{entity}>",
                        $"{relativePath}:{lineNumber}: {owningProject} accesses {entity} DbSet owned by {owner}"
                    );
                }
            }
        }
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

                if (
                    owningProject == "Agw.Infrastructure"
                    && referencedNamespace.EndsWith(".Application.Persistence", StringComparison.Ordinal)
                )
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

    private static bool DeclaresNamespace(string sourceFile, string expectedNamespace) =>
        File.ReadLines(sourceFile)
            .Select(line => NamespaceDeclarationRegex().Match(line))
            .Any(match =>
                match.Success
                && string.Equals(match.Groups["namespace"].Value, expectedNamespace, StringComparison.Ordinal)
            );

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

    private static readonly IReadOnlyDictionary<string, string> EntityOwners = new Dictionary<string, string>(
        StringComparer.Ordinal
    )
    {
        ["Agent"] = "Agw.Agents",
        ["AgentConnectionRelation"] = "Agw.Agents",
        ["AgentMcpServerRelation"] = "Agw.Agents",
        ["AgentSessionStateEntry"] = "Agw.Agents",
        ["Agentflow"] = "Agw.Agents",
        ["AgentflowEdge"] = "Agw.Agents",
        ["AgentflowNode"] = "Agw.Agents",
        ["AgentflowTrace"] = "Agw.Agents",
        ["AgentflowCheckpointRecord"] = "Agw.Agents",
        ["DurableExecutionEventRecord"] = "Agw.Agents",
        ["DurableExecutionRecord"] = "Agw.Agents",
        ["McpServer"] = "Agw.Agents",
        ["AgentSkillRelation"] = "Agw.Agents",
        ["ApiToken"] = "Agw.Auth",
        ["Connection"] = "Agw.Integrations",
        ["ConnectionCredential"] = "Agw.Integrations",
        ["PluginInstallation"] = "Agw.Integrations",
        ["PluginInstallationCredential"] = "Agw.Integrations",
        ["Project"] = "Agw.Projects",
        ["ProjectConnectionRelation"] = "Agw.Projects",
        ["ProjectConversation"] = "Agw.Projects",
        ["ProjectConversationChatHistory"] = "Agw.Projects",
        ["ProjectMcpServerRelation"] = "Agw.Projects",
        ["ProjectSkillRelation"] = "Agw.Projects",
        ["ProjectConversationBinding"] = "Agw.Projects",
        ["AgentUsage"] = "Agw.Projects",
        ["Job"] = "Agw.Jobs",
        ["JobLog"] = "Agw.Jobs",
        ["AgwAiModel"] = "Agw.Providers",
        ["ModelProviderRelation"] = "Agw.Providers",
        ["Provider"] = "Agw.Providers",
        ["ProviderAuthConfig"] = "Agw.Providers",
        ["RemoteSkillCache"] = "Agw.Skills",
        ["Skill"] = "Agw.Skills",
        ["ProjectMemoryEntry"] = "Agw.Tools",
        ["UserMemory"] = "Agw.Tools",
    };

    private static readonly IReadOnlyDictionary<string, int> AllowedLegacyForeignPersistenceAccessCounts =
        new Dictionary<string, int>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> LegacyCrossModuleServiceOwners = new Dictionary<
        string,
        string
    >(StringComparer.Ordinal)
    {
        ["IProjectAppService"] = "Agw.Projects",
        ["ITaskAppService"] = "Agw.Projects",
        ["ITaskSessionBindingService"] = "Agw.Projects",
    };

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

    [GeneratedRegex(
        @"^\s*(?:public|internal|private|protected)?\s*(?:sealed\s+|abstract\s+|partial\s+)*(?:class|interface|record)\s+(?<type>[A-Za-z_][A-Za-z0-9_]*(?:AppService|RuntimeService))\b",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex ImplementationTypeDeclarationRegex();

    [GeneratedRegex(@"\b(?<type>[A-Za-z_][A-Za-z0-9_]*(?:AppService|RuntimeService))\b", RegexOptions.CultureInvariant)]
    private static partial Regex ImplementationTypeReferenceRegex();

    [GeneratedRegex(
        @"\b(?<type>IProjectAppService|ITaskAppService|ITaskSessionBindingService)\b",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex LegacyCrossModuleServiceReferenceRegex();

    [GeneratedRegex(
        @"\bIRepository\s*<\s*(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\.)*(?<entity>[A-Za-z_][A-Za-z0-9_]*)\s*>",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex OwnedRepositoryRegex();

    [GeneratedRegex(
        @"\b(?:DbSet|Set)\s*<\s*(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\.)*(?<entity>[A-Za-z_][A-Za-z0-9_]*)\s*>",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex OwnedDbSetAccessRegex();
}
