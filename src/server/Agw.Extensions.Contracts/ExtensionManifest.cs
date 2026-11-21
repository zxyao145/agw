using System.Text.Json;

namespace Agw.Extensions.Contracts;

[Flags]
public enum ExtensionTarget
{
    None = 0,
    Server = 1,
    Desktop = 2,
    Web = 4,
    Mobile = 8,
}

public sealed record ExtensionManifest
{
    public int ManifestVersion { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Version { get; init; }
    public ExtensionTarget Targets { get; init; }
    public ExtensionServerManifest? Server { get; init; }
    public JsonElement? Desktop { get; init; }
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.Ordinal);
}

public sealed record ExtensionServerManifest
{
    public int ApiVersion { get; init; }
    public string? MinHostVersion { get; init; }
    public required string EntryAssembly { get; init; }
    public required string EntryType { get; init; }

    public ExtensionServerPermissions Permissions { get; init; } = new();
}

public sealed record ExtensionServerPermissions
{
    public ExtensionEventPermissions Events { get; init; } = new();
}
