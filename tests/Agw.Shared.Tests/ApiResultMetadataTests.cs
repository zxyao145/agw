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
    public void ControllerActions_ReturningAgwApiResultDeclareProducesApiResult()
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
                if (!actionSource.Contains("AgwApiResult.", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!attributes.Contains("ProducesApiResult", StringComparison.Ordinal))
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
