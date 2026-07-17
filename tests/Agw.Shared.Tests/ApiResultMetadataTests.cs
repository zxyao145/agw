using System.Text.RegularExpressions;

using Agw.Shared.Results;

using Bens.Results;

using Microsoft.AspNetCore.Http;

namespace Agw.Shared.Tests;

public class ApiResultMetadataTests
{
    [Fact]
    public void ProducesApiResultAttribute_WithDataType_UsesGenericApiResultEnvelope()
    {
        var attribute = new ProducesApiResultAttribute(typeof(string));

        Assert.Equal(typeof(ApiResult<string>), attribute.Type);
        Assert.Equal(StatusCodes.Status200OK, attribute.StatusCode);
    }

    [Fact]
    public void ProducesApiResultAttribute_WithoutDataType_UsesPlainApiResultEnvelope()
    {
        var attribute = new ProducesApiResultAttribute();

        Assert.Equal(typeof(ApiResult), attribute.Type);
        Assert.Equal(StatusCodes.Status200OK, attribute.StatusCode);
    }

    [Fact]
    public void ControllerActions_ReturningApiResultDeclareEnvelopeMetadata()
    {
        var repoRoot = FindRepositoryRoot();
        var backendRoot = Path.Combine(repoRoot, "src", "server");
        var actionPattern = CreateHttpActionPattern();
        var violations = new List<string>();

        foreach (var filePath in Directory.EnumerateFiles(backendRoot, "*Controller.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(filePath);
            var matches = actionPattern.Matches(source);
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var attributes = match.Groups["attributes"].Value;
                if (!attributes.Contains("[Http", StringComparison.Ordinal))
                {
                    continue;
                }

                var actionSourceEnd = i + 1 < matches.Count
                    ? matches[i + 1].Index
                    : source.Length;
                var actionSource = source[match.Index..actionSourceEnd];
                var returnsApiResult =
                    actionSource.Contains("ApiResult.", StringComparison.Ordinal)
                    || actionSource.Contains(".ToApiResult(", StringComparison.Ordinal);
                if (!returnsApiResult)
                {
                    continue;
                }

                var hasEnvelopeMetadata =
                    attributes.Contains("ProducesApiResult", StringComparison.Ordinal)
                    || attributes.Contains("ProducesResponseType(typeof(ApiResult", StringComparison.Ordinal);
                if (!hasEnvelopeMetadata)
                {
                    var relativePath = Path.GetRelativePath(repoRoot, filePath);
                    var methodName = match.Groups["name"].Value;
                    violations.Add($"{relativePath}: {methodName}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void BackendSource_DoesNotReferenceAgwApiResult()
    {
        var repoRoot = FindRepositoryRoot();
        var backendRoot = Path.Combine(repoRoot, "src", "server");
        var violations = Directory
            .EnumerateFiles(backendRoot, "*.cs", SearchOption.AllDirectories)
            .Where(filePath => File.ReadAllText(filePath).Contains("AgwApiResult", StringComparison.Ordinal))
            .Select(filePath => Path.GetRelativePath(repoRoot, filePath))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void RepositoryRules_RequireDirectApiResultUsage()
    {
        var repoRoot = FindRepositoryRoot();
        var ruleFiles = new[]
        {
            Path.Combine(repoRoot, "AGENTS.md"),
            Path.Combine(repoRoot, "CLAUDE.md"),
            Path.Combine(repoRoot, "docs", "rules.md")
        };

        foreach (var ruleFile in ruleFiles)
        {
            var source = File.ReadAllText(ruleFile);
            Assert.DoesNotContain("AgwApiResult", source, StringComparison.Ordinal);
            Assert.Contains("ApiResult.Ok", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ControllerActions_DeclareProducesMetadata()
    {
        var repoRoot = FindRepositoryRoot();
        var backendRoot = Path.Combine(repoRoot, "src", "server");
        var actionPattern = CreateHttpActionPattern();
        var violations = new List<string>();

        foreach (var filePath in Directory.EnumerateFiles(backendRoot, "*Controller.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(filePath);
            var matches = actionPattern.Matches(source);
            foreach (Match match in matches)
            {
                var attributes = match.Groups["attributes"].Value;
                if (!attributes.Contains("[Http", StringComparison.Ordinal))
                {
                    continue;
                }

                var hasProducesMetadata =
                    attributes.Contains("ProducesApiResult", StringComparison.Ordinal)
                    || attributes.Contains("ProducesResponseType", StringComparison.Ordinal);
                if (hasProducesMetadata)
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(repoRoot, filePath);
                var methodName = match.Groups["name"].Value;
                violations.Add($"{relativePath}: {methodName}");
            }
        }

        Assert.Empty(violations);
    }

    private static Regex CreateHttpActionPattern()
    {
        return new Regex(
            @"(?<attributes>(?:\s*\[[^\]]+\]\s*)+)\s*public\s+(?:async\s+)?(?:Task(?:<[^>]+>)?|ActionResult<[^>]+>|IActionResult)\s+(?<name>[A-Za-z0-9_]+)\s*\(",
            RegexOptions.Compiled | RegexOptions.Multiline);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Agw.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root.");
    }
}
