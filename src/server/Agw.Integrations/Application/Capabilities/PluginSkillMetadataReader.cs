using System.Text.Json;
using System.Text.RegularExpressions;

using Agw.Integrations.Domain.Plugins;

namespace Agw.Integrations.Application.Capabilities;

public sealed partial class PluginSkillMetadataReader
{
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;

    private readonly IPluginContentRootProvider _contentRootProvider;

    public PluginSkillMetadataReader(IPluginContentRootProvider contentRootProvider)
    {
        _contentRootProvider = contentRootProvider;
    }

    public bool TryRead(PluginSkillDefinition definition, out PluginSkillMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(definition);
        metadata = null!;

        try
        {
            if (!TryResolveSkillFile(definition.ContentPath, out var skillFilePath))
            {
                return false;
            }

            var content = File.ReadAllText(skillFilePath);
            if (!TryReadFrontmatter(content, out var name, out var description))
            {
                return false;
            }

            metadata = new PluginSkillMetadata
            {
                Id = name,
                Description = description,
                SkillFilePath = skillFilePath
            };
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private bool TryResolveSkillFile(string contentPath, out string skillFilePath)
    {
        skillFilePath = string.Empty;
        if (string.IsNullOrWhiteSpace(contentPath) || Path.IsPathRooted(contentPath))
        {
            return false;
        }

        var root = Path.GetFullPath(_contentRootProvider.ContentRoot);
        var path = Path.GetFullPath(Path.Combine(root, contentPath));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
        {
            return false;
        }

        var physicalRoot = ResolvePhysicalPath(root, []);
        var physicalPath = ResolvePhysicalPath(root, relative.Split(Path.DirectorySeparatorChar));
        if (!IsWithinDirectory(physicalRoot, physicalPath))
        {
            return false;
        }

        skillFilePath = path;
        return true;
    }

    private static bool TryReadFrontmatter(string content, out string name, out string description)
    {
        name = string.Empty;
        description = string.Empty;
        var normalized = content.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length < 3 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
        {
            return false;
        }

        var closingDelimiter = Array.FindIndex(
            lines,
            1,
            line => string.Equals(line.Trim(), "---", StringComparison.Ordinal));
        if (closingDelimiter < 0)
        {
            return false;
        }

        for (var index = 1; index < closingDelimiter; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = ParseScalar(line[(separator + 1)..]);
            if (value == null)
            {
                return false;
            }

            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
            {
                if (name.Length > 0)
                {
                    return false;
                }

                name = value;
            }
            else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase))
            {
                if (description.Length > 0)
                {
                    return false;
                }

                description = value;
            }
        }

        return name.Length is > 0 and <= MaxNameLength
            && SkillNamePattern().IsMatch(name)
            && description.Length is > 0 and <= MaxDescriptionLength;
    }

    private static string? ParseScalar(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.StartsWith('"') || trimmed.EndsWith('"'))
        {
            if (!(trimmed.StartsWith('"') && trimmed.EndsWith('"')))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<string>(trimmed)?.Trim();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (trimmed.StartsWith('\'') || trimmed.EndsWith('\''))
        {
            if (!(trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
            {
                return null;
            }

            return trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal).Trim();
        }

        return trimmed;
    }

    private static string ResolvePhysicalPath(string root, IReadOnlyList<string> segments)
    {
        var rootInfo = new DirectoryInfo(root);
        var rootTarget = rootInfo.LinkTarget == null ? null : rootInfo.ResolveLinkTarget(returnFinalTarget: true);
        var current = Path.GetFullPath(rootTarget?.FullName ?? root);
        for (var index = 0; index < segments.Count; index++)
        {
            var candidate = Path.Combine(current, segments[index]);
            FileSystemInfo info = index == segments.Count - 1
                ? new FileInfo(candidate)
                : new DirectoryInfo(candidate);
            var target = info.LinkTarget == null ? null : info.ResolveLinkTarget(returnFinalTarget: true);
            current = Path.GetFullPath(target?.FullName ?? candidate);
        }

        return current;
    }

    private static bool IsWithinDirectory(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(root, path, comparison)
            || path.StartsWith(
                string.Concat(root.TrimEnd(Path.DirectorySeparatorChar), Path.DirectorySeparatorChar),
                comparison);
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillNamePattern();
}

public sealed class PluginSkillMetadata
{
    public required string Id { get; init; }

    public required string Description { get; init; }

    public required string SkillFilePath { get; init; }
}
