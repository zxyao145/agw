using System.Text.Json;

namespace Agw.Extensions.Contracts;

public interface IExtensionHostIdentity
{
    string HostId { get; }
    string Role { get; }
}

public interface IExtensionDeploymentIdentity
{
    Task<string> GetServerIdAsync(CancellationToken cancellationToken = default);
}

public sealed class ExtensionDeploymentSettings
{
    public const string Key = "server.identity";

    public string ServerId { get; init; } = "";
}

public sealed record ExtensionCatalog(string ServerId, long Revision, IReadOnlyList<ExtensionCatalogEntry> Extensions);

public sealed record ExtensionCatalogEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Version { get; init; }
    public required string ReleaseHash { get; init; }
    public required string PackageHash { get; init; }
    public required JsonElement Manifest { get; init; }
}
