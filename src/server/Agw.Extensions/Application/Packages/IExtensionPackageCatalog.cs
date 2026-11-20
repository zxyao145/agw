using Agw.Extensions.Contracts;

namespace Agw.Extensions.Application.Packages;

public sealed record ExtensionLocalPackage
{
    public required Guid ExtensionId { get; init; }
    public required ExtensionManifest Manifest { get; init; }
    public required string ManifestJson { get; init; }
    public required string ReleaseHash { get; init; }
    public required string PackageHash { get; init; }
    public required string SnapshotDirectory { get; init; }
    public required string ArchivePath { get; init; }
}

public interface IExtensionPackageCatalog
{
    IReadOnlyList<ExtensionLocalPackage> GetEntries();

    (long Revision, IReadOnlyList<ExtensionCatalogEntry> Entries) GetDesktopCatalog(
        long? afterRevision,
        IReadOnlySet<Guid> activeDesktopExtensions
    );

    Task<Stream> GetDesktopPackageAsync(Guid extensionId, string releaseHash, CancellationToken cancellationToken);
}
