using System.Text.RegularExpressions;
using Xunit;

namespace Agw.Architecture.Tests;

public sealed partial class BackendArchitectureTests
{
    [Fact]
    public void AnemicDataObjects_BusinessMembersMatchLegacyAllowlist()
    {
        // Arrange
        var serverRoot = GetServerRoot();
        var dataEntityRoot = Path.Combine(serverRoot, "Agw.Data", "Entities");
        var persistedEntities = Directory
            .EnumerateFiles(dataEntityRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("Configuration.cs", StringComparison.Ordinal));
        var moduleDomainData = GetSourceFiles(serverRoot)
            .Where(path =>
                HasPathSegment(Path.GetRelativePath(serverRoot, path), "Domain")
                && !HasPathSegment(Path.GetRelativePath(serverRoot, path), "Behaviors")
                && HasAnyPathSegment(
                    Path.GetRelativePath(serverRoot, path),
                    "Entity",
                    "Entities",
                    "ValueObject",
                    "ValueObjects",
                    "Decision",
                    "Decisions",
                    "Snapshot",
                    "Snapshots"
                )
            );

        // Act
        var actualMembers = persistedEntities
            .Concat(moduleDomainData)
            .SelectMany(path => FindDataBehaviorMembers(serverRoot, path))
            .OrderBy(static member => member, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Equal(
            AllowedLegacyDataBehaviorMembers.OrderBy(static member => member, StringComparer.Ordinal),
            actualMembers
        );
    }

    [Fact]
    public void EntitySpecificDomainServices_MatchLegacyAllowlist()
    {
        // Arrange
        var serverRoot = GetServerRoot();

        // Act
        var actualServices = GetSourceFiles(serverRoot)
            .SelectMany(path =>
                EntitySpecificDomainServiceRegex()
                    .Matches(File.ReadAllText(path))
                    .Select(match => match.Groups["type"].Value)
                    .Where(type => EntityDomainServiceTypeNames.Contains(RemoveSuffix(type, "DomainService")))
                    .Select(type => $"{NormalizePath(Path.GetRelativePath(serverRoot, path))}:{type}")
            )
            .OrderBy(static service => service, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Equal(
            AllowedLegacyEntityDomainServices.OrderBy(static service => service, StringComparer.Ordinal),
            actualServices
        );
    }

    [Fact]
    public void EntityBehaviors_FollowConstructionAndDependencyRules()
    {
        // Arrange
        var serverRoot = GetServerRoot();

        // Act
        var violations = GetSourceFiles(serverRoot)
            .SelectMany(path => FindEntityBehaviorViolations(serverRoot, path))
            .OrderBy(static violation => violation, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public void EntityBehaviors_AreNeverRegisteredWithIoc()
    {
        // Arrange
        var serverRoot = GetServerRoot();

        // Act
        var violations = GetSourceFiles(serverRoot)
            .SelectMany(path => FindEntityBehaviorIocViolations(serverRoot, path))
            .OrderBy(static violation => violation, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Empty(violations);
    }

    private static IEnumerable<string> FindDataBehaviorMembers(string serverRoot, string sourceFile)
    {
        var relativePath = NormalizePath(Path.GetRelativePath(serverRoot, sourceFile));
        var source = File.ReadAllText(sourceFile);

        foreach (Match match in DataMethodDeclarationRegex().Matches(source))
        {
            yield return $"{relativePath}:{match.Groups["member"].Value}";
        }

        foreach (Match match in DataOperatorDeclarationRegex().Matches(source))
        {
            yield return $"{relativePath}:operator {match.Groups["operator"].Value}";
        }

        foreach (Match match in ComputedDataPropertyRegex().Matches(source))
        {
            yield return $"{relativePath}:{match.Groups["member"].Value}";
        }

        foreach (Match match in CustomDataAccessorRegex().Matches(source))
        {
            yield return $"{relativePath}:custom {match.Groups["accessor"].Value} accessor";
        }
    }

    private static IEnumerable<string> FindEntityBehaviorViolations(string serverRoot, string sourceFile)
    {
        var source = File.ReadAllText(sourceFile);
        var behaviorMatches = EntityBehaviorClassRegex()
            .Matches(source)
            .Where(match => EntityBehaviorTypeNames.Contains(match.Groups["entity"].Value))
            .ToArray();
        var behaviorInterfaces = BehaviorInterfaceRegex()
            .Matches(source)
            .Select(match => match.Groups["type"].Value)
            .Where(type => EntityBehaviorTypeNames.Contains(type[1..^"Behavior".Length]))
            .ToArray();
        var relativePath = NormalizePath(Path.GetRelativePath(serverRoot, sourceFile));

        foreach (var behaviorInterface in behaviorInterfaces)
        {
            yield return $"{relativePath}: Behavior Interface {behaviorInterface} is forbidden";
        }

        if (behaviorMatches.Length == 0)
        {
            yield break;
        }

        if (!relativePath.Contains("/Domain/Behaviors/", StringComparison.Ordinal))
        {
            yield return $"{relativePath}: entity Behavior must live under Domain/Behaviors";
        }

        var owningProject = GetOwningProject(serverRoot, sourceFile);
        foreach (
            var match in behaviorMatches.Where(match => EntityOwners[match.Groups["entity"].Value] != owningProject)
        )
        {
            yield return $"{relativePath}: {match.Groups["entity"].Value}Behavior must live in {EntityOwners[match.Groups["entity"].Value]}";
        }

        foreach (var match in behaviorMatches.Where(match => match.Groups["sealed"].Length == 0))
        {
            yield return $"{relativePath}: {match.Groups["entity"].Value}Behavior must be sealed";
        }

        foreach (var match in behaviorMatches.Where(match => match.Groups["kind"].Value != "class"))
        {
            yield return $"{relativePath}: {match.Groups["entity"].Value}Behavior must be a class";
        }

        foreach (
            Match match in BehaviorPrimaryConstructorRegex()
                .Matches(source)
                .Where(match => EntityBehaviorTypeNames.Contains(match.Groups["entity"].Value))
        )
        {
            yield return $"{relativePath}: {match.Groups["entity"].Value}Behavior must use an explicit constructor";
        }

        foreach (var match in behaviorMatches)
        {
            var entity = match.Groups["entity"].Value;
            var constructors = BehaviorConstructorRegex()
                .Matches(source)
                .Where(constructor => constructor.Groups["entity"].Value == entity)
                .ToArray();
            if (constructors.Length != 1 || !BindsOnlyRoot(constructors[0].Groups["parameters"].Value, entity))
            {
                yield return $"{relativePath}: {entity}Behavior must declare exactly one constructor that binds one {entity} root";
            }
        }

        foreach (Match match in ForbiddenBehaviorDependencyRegex().Matches(source))
        {
            yield return $"{relativePath}: Behavior references forbidden dependency {match.Value}";
        }
    }

    private static IEnumerable<string> FindEntityBehaviorIocViolations(string serverRoot, string sourceFile)
    {
        var relativePath = NormalizePath(Path.GetRelativePath(serverRoot, sourceFile));
        foreach (Match match in BehaviorIocRegistrationRegex().Matches(File.ReadAllText(sourceFile)))
        {
            var behaviorType = match.Groups["type"].Value;
            if (EntityBehaviorTypeNames.Contains(RemoveSuffix(behaviorType, "Behavior")))
            {
                yield return $"{relativePath}: IoC registration for {behaviorType} is forbidden";
            }
        }
    }

    private static bool HasAnyPathSegment(string path, params string[] expectedSegments) =>
        expectedSegments.Any(expected => HasPathSegment(path, expected));

    private static string RemoveSuffix(string value, string suffix) => value[..^suffix.Length];

    private static bool BindsOnlyRoot(string parameters, string entity)
    {
        var tokens = parameters.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 2 || parameters.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var parameterType = tokens[0].Replace("global::", string.Empty, StringComparison.Ordinal);
        return parameterType.Split('.').Last() == entity;
    }

    private static readonly IReadOnlySet<string> AllowedLegacyDataBehaviorMembers = Set(
        "Agw.Data/Entities/Auth/ApiToken.cs:NormalizeName",
        "Agw.Data/Entities/Projects/Project.cs:GetMustWorkspace"
    );

    private static readonly IReadOnlySet<string> AllowedLegacyEntityDomainServices = Set();

    private static readonly IReadOnlySet<string> EntityBehaviorTypeNames = EntityOwners.Keys.ToHashSet(
        StringComparer.Ordinal
    );

    private static readonly IReadOnlySet<string> EntityDomainServiceTypeNames = EntityOwners
        .Keys.Concat(["McpToolServer", "Model", "ModelProvider"])
        .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(
        @"^\s*(?:public|internal|protected|private)\s+(?:static\s+)?(?:async\s+)?[A-Za-z_][A-Za-z0-9_<>,?.\[\] ]*\s+(?<member>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Multiline | RegexOptions.CultureInvariant
    )]
    private static partial Regex DataMethodDeclarationRegex();

    [GeneratedRegex(
        @"^\s*(?:public|internal|protected|private)\s+(?:static\s+)?[A-Za-z_][A-Za-z0-9_<>,?.\[\] ]*\s+operator\s+(?<operator>[^\s(]+)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant
    )]
    private static partial Regex DataOperatorDeclarationRegex();

    [GeneratedRegex(
        @"^\s*(?:public|internal|protected|private)\s+[A-Za-z_][A-Za-z0-9_<>,?.\[\] ]*\s+(?<member>[A-Za-z_][A-Za-z0-9_]*)\s*=>",
        RegexOptions.Multiline | RegexOptions.CultureInvariant
    )]
    private static partial Regex ComputedDataPropertyRegex();

    [GeneratedRegex(@"\b(?<accessor>get|set|init)\s*(?:=>|\{)", RegexOptions.CultureInvariant)]
    private static partial Regex CustomDataAccessorRegex();

    [GeneratedRegex(@"\bclass\s+(?<type>[A-Za-z_][A-Za-z0-9_]*DomainService)\b", RegexOptions.CultureInvariant)]
    private static partial Regex EntitySpecificDomainServiceRegex();

    [GeneratedRegex(
        @"\b(?<sealed>sealed\s+)?(?<kind>class|record(?:\s+(?:class|struct))?|struct)\s+(?<entity>[A-Za-z_][A-Za-z0-9_]*)Behavior\b",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex EntityBehaviorClassRegex();

    [GeneratedRegex(@"\bclass\s+(?<entity>[A-Za-z_][A-Za-z0-9_]*)Behavior\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex BehaviorPrimaryConstructorRegex();

    [GeneratedRegex(
        @"\b(?:public|internal|protected|private)\s+(?<entity>[A-Za-z_][A-Za-z0-9_]*)Behavior\s*\((?<parameters>[^)]*)\)",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex BehaviorConstructorRegex();

    [GeneratedRegex(@"\binterface\s+(?<type>I[A-Za-z_][A-Za-z0-9_]*Behavior)\b", RegexOptions.CultureInvariant)]
    private static partial Regex BehaviorInterfaceRegex();

    [GeneratedRegex(
        @"\b(?:Microsoft\.(?:EntityFrameworkCore|AspNetCore|Agents)|ModelContextProtocol|System\.IO|Domain\.Policies|DbContext|I?[A-Za-z_][A-Za-z0-9_]*Repository|IServiceProvider|HttpClient|TimeProvider|IUserInfoService|UserInfoUtil|File|Directory|FileInfo|DirectoryInfo|FileStream|[A-Za-z_][A-Za-z0-9_]*(?:Policy|DomainService)|Agw\.Infrastructure)\b",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex ForbiddenBehaviorDependencyRegex();

    [GeneratedRegex(
        @"\b(?:TryAdd|Add)(?:Scoped|Transient|Singleton)\s*(?:<[^>\r\n]*\b(?<type>[A-Za-z_][A-Za-z0-9_]*Behavior)\b[^>]*>|\([^\r\n;]*\btypeof\(\s*(?<type>[A-Za-z_][A-Za-z0-9_]*Behavior)\s*\))",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex BehaviorIocRegistrationRegex();
}
