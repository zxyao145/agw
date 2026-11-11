using System.Text.Json;
using System.Text.RegularExpressions;
using Agw.Extensions.Contracts;
using Agw.Shared.Exceptions;
using Semver;

namespace Agw.Extensions.Infrastructure.Packages;

public static partial class ExtensionManifestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ExtensionManifest Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var manifestVersion = root.GetProperty("manifestVersion").GetInt32();
            var manifest =
                manifestVersion == 1
                    ? ReadDesktopV1(root)
                    : JsonSerializer.Deserialize<ExtensionManifest>(json, JsonOptions)!;
            Validate(manifest);
            return manifest;
        }
        catch (Exception exception)
            when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "The extension manifest is malformed.");
        }
    }

    public static void ValidatePath(string path)
    {
        if (
            string.IsNullOrWhiteSpace(path)
            || path.Contains('\\')
            || path.Contains(':')
            || path.Contains('%')
            || path.Contains('?')
            || path.Contains('#')
            || path.Any(char.IsControl)
            || path.Split('/').Any(segment => segment is "" or "." or "..")
        )
            throw new AgwException(
                ErrorCodes.ExtensionPackageInvalid,
                "Package paths must be relative, normalized paths."
            );
    }

    private static ExtensionManifest ReadDesktopV1(JsonElement root) =>
        new()
        {
            ManifestVersion = 1,
            Id = root.GetProperty("id").GetString()!,
            Name = "",
            DisplayName = root.GetProperty("name").GetString()!,
            Version = root.GetProperty("version").GetString()!,
            Targets = ExtensionTarget.Desktop,
            Desktop = root.Clone(),
        };

    private static void Validate(ExtensionManifest manifest)
    {
        if (
            manifest is null
            || manifest.ManifestVersion is not (1 or 2)
            || !Guid.TryParseExact(manifest.Id, "D", out var id)
            || id.Version != 7
            || id.ToString() != manifest.Id
            || string.IsNullOrWhiteSpace(manifest.DisplayName)
            || manifest.Files is null
            || !SemVersion.TryParse(manifest.Version, SemVersionStyles.Strict, out _)
        )
            throw new AgwException(
                ErrorCodes.ExtensionPackageInvalid,
                "Extension identity, version or manifest version is invalid."
            );
        if (
            manifest.ManifestVersion == 2
            && (
                string.IsNullOrEmpty(manifest.Name)
                || !TechnicalName().IsMatch(manifest.Name)
                || manifest.Name.Length > 64
            )
        )
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension technical name is invalid.");
        if (
            manifest.Targets
            is not (
                ExtensionTarget.Server
                or ExtensionTarget.Desktop
                or (ExtensionTarget.Server | ExtensionTarget.Desktop)
            )
        )
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension target flags are unsupported.");
        if (
            manifest.Targets.HasFlag(ExtensionTarget.Server) != (manifest.Server is not null)
            || manifest.Targets.HasFlag(ExtensionTarget.Desktop) != (manifest.Desktop is not null)
        )
            throw new AgwException(
                ErrorCodes.ExtensionPackageInvalid,
                "Platform entries must match the declared targets."
            );
        if (manifest.Server is { } server)
        {
            if (
                server.ApiVersion <= 0
                || string.IsNullOrWhiteSpace(server.EntryType)
                || server.MinHostVersion is { } minimum && !SemVersion.TryParse(minimum, SemVersionStyles.Strict, out _)
            )
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Server API version or entry is invalid.");
            ValidatePath(server.EntryAssembly);
            if (!server.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Server entry must be an assembly.");
            ValidateEventPermissions(server.Permissions?.Events);
        }
        if (manifest.Desktop is { } desktop && desktop.GetProperty("apiVersion").GetInt32() <= 0)
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Desktop API version is unsupported.");
        foreach (var (path, hash) in manifest.Files)
        {
            ValidatePath(path);
            if (path == "manifest.json" || hash is null || !Sha256().IsMatch(hash))
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "File checksum is invalid.");
        }
        if (manifest.Targets == (ExtensionTarget.Server | ExtensionTarget.Desktop))
            ValidateDesktopResources(manifest.Desktop!.Value, manifest.Files);
    }

    private static void ValidateDesktopResources(JsonElement element, IReadOnlyDictionary<string, string> files)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ValidateDesktopResources(item, files);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
            return;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is "entry" or "background" or "path" or "icon")
            {
                var path = property.Value.GetString();
                if (property.Name == "icon" && path is not null && !path.Contains('.') && !path.Contains('/'))
                    continue;
                if (path is null || !path.StartsWith("desktop/", StringComparison.Ordinal) || !files.ContainsKey(path))
                    throw new AgwException(
                        ErrorCodes.ExtensionPackageInvalid,
                        "Combined extension Desktop resources must be checksummed files under desktop/."
                    );
            }
            else
                ValidateDesktopResources(property.Value, files);
        }
    }

    private static void ValidateEventPermissions(ExtensionEventPermissions? permissions)
    {
        if (permissions is null || permissions.Publish is null)
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension event permissions are invalid.");
        }

        foreach (string? pattern in permissions.Publish)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension event permission is invalid.");
            }

            bool wildcard = pattern.EndsWith(".*", StringComparison.Ordinal);
            string eventType = wildcard ? pattern[..^2] : pattern;
            if (
                string.IsNullOrWhiteSpace(pattern)
                || pattern != pattern.Trim()
                || eventType.Length == 0
                || pattern.StartsWith("agw.", StringComparison.Ordinal)
                || pattern.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
                || pattern.Contains('*') && (!wildcard || pattern[..^2].Contains('*'))
            )
            {
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension event permission is invalid.");
            }
        }
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalName();

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();
}
