using System.Text.Json;

namespace PiAgentSdk.Internal;

internal sealed class PiProcessTarget
{
    private const string PiPackageName = "pi-coding-agent";
    private const string PiPackageScope = "@earendil-works";

    private PiProcessTarget(string fileName, IReadOnlyList<string> argumentList)
    {
        FileName = fileName;
        ArgumentList = argumentList;
    }

    public string FileName { get; }

    public IReadOnlyList<string> ArgumentList { get; }

    public static PiProcessTarget Resolve(string executable, IReadOnlyList<string> arguments, bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        if (!isWindows || !IsCommandScript(executable))
        {
            return new PiProcessTarget(executable, arguments.ToArray());
        }

        // Avoid cmd.exe /c: percent expansion and shell metacharacter parsing cannot preserve arbitrary CLI arguments.
        var entrypoint = ResolveNpmEntrypoint(executable);
        if (entrypoint == null)
        {
            throw new InvalidOperationException(
                $"Pi command script '{executable}' could not be resolved to a trusted npm Node entrypoint."
            );
        }

        var scriptDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var adjacentNode = Path.Combine(scriptDirectory, "node.exe");
        var node = File.Exists(adjacentNode) ? adjacentNode : "node";
        return new PiProcessTarget(node, [entrypoint, .. arguments]);
    }

    private static bool IsCommandScript(string executable)
    {
        var extension = Path.GetExtension(executable);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveNpmEntrypoint(string executable)
    {
        var scriptDirectory = Path.GetDirectoryName(Path.GetFullPath(executable));
        if (scriptDirectory == null)
        {
            return null;
        }

        var packageRoots = new[]
        {
            Path.Combine(scriptDirectory, "node_modules", PiPackageScope, PiPackageName),
            Path.GetFullPath(Path.Combine(scriptDirectory, "..", PiPackageScope, PiPackageName)),
        };
        foreach (var packageRoot in packageRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entrypoint = ReadPackageEntrypoint(packageRoot);
            if (entrypoint != null)
            {
                return entrypoint;
            }
        }

        return null;
    }

    private static string? ReadPackageEntrypoint(string packageRoot)
    {
        var packageJsonPath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return null;
        }

        try
        {
            using var package = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!package.RootElement.TryGetProperty("bin", out var bin))
            {
                return null;
            }

            string? relativeEntrypoint = null;
            if (bin.ValueKind == JsonValueKind.String)
            {
                relativeEntrypoint = bin.GetString();
            }
            else if (
                bin.ValueKind == JsonValueKind.Object
                && bin.TryGetProperty("pi", out var piBin)
                && piBin.ValueKind == JsonValueKind.String
            )
            {
                relativeEntrypoint = piBin.GetString();
            }

            if (string.IsNullOrWhiteSpace(relativeEntrypoint))
            {
                return null;
            }

            var canonicalRoot = Path.GetFullPath(packageRoot);
            var entrypoint = Path.GetFullPath(Path.Combine(canonicalRoot, relativeEntrypoint));
            return IsContained(canonicalRoot, entrypoint) && File.Exists(entrypoint) ? entrypoint : null;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or JsonException
                        or ArgumentException
                        or NotSupportedException
            )
        {
            return null;
        }
    }

    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
