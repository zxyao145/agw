using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Extensions.Application.Packages;
using Agw.Extensions.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agw.Extensions.Infrastructure.Packages;

public sealed class ExtensionPackageCatalog : IExtensionPackageCatalog
{
    private readonly IHostEnvironment _environment;
    private readonly ExtensionOptions _options;
    private readonly ExtensionPackageCache _packages;
    private IReadOnlyList<ExtensionLocalPackage>? _entries;

    public ExtensionPackageCatalog(
        IHostEnvironment environment,
        ExtensionPackageCache packages,
        IOptions<ExtensionOptions> options
    )
    {
        _environment = environment;
        _packages = packages;
        _options = options.Value;
    }

    public IReadOnlyList<ExtensionLocalPackage> GetEntries() =>
        _entries
        ?? throw new AgwException(
            ErrorCodes.ExtensionUnavailable,
            "The extension package catalog has not been initialized."
        );

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_entries is not null)
            return;

        var directory = Path.GetFullPath(_options.Directory, _environment.ContentRootPath);
        if (!System.IO.Directory.Exists(directory))
        {
            _entries = Array.AsReadOnly(Array.Empty<ExtensionLocalPackage>());
            return;
        }
        var entries = new List<ExtensionLocalPackage>();
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            var childDirectory in System.IO.Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryName = Path.GetFileName(childDirectory);
            if (directoryName.StartsWith(".", StringComparison.Ordinal))
                continue;

            var entry = await ReadChildAsync(childDirectory, cancellationToken);
            if (!ids.Add(entry.ExtensionId))
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension ids must be unique.");
            if (!names.Add(entry.Manifest.Name))
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension technical names must be unique.");
            entries.Add(entry);
        }
        _entries = Array.AsReadOnly(entries.ToArray());
    }

    public ExtensionLocalPackage GetValidPackage(Guid extensionId)
    {
        var package = GetEntries().SingleOrDefault(entry => entry.ExtensionId == extensionId);
        return package ?? throw new AgwException(ErrorCodes.ExtensionNotFound);
    }

    public (long Revision, IReadOnlyList<ExtensionCatalogEntry> Entries) GetDesktopCatalog(
        long? afterRevision,
        IReadOnlySet<Guid> activeDesktopExtensions
    )
    {
        var entries = GetEntries()
            .Where(package =>
                package.Manifest.Targets.HasFlag(ExtensionTarget.Desktop)
                && activeDesktopExtensions.Contains(package.ExtensionId)
            )
            .OrderBy(package => package.ExtensionId)
            .Select(package =>
            {
                using var document = JsonDocument.Parse(package.ManifestJson!);
                return new ExtensionCatalogEntry
                {
                    Id = package.Manifest.Id,
                    Name = package.Manifest.Name,
                    DisplayName = package.Manifest.DisplayName,
                    Version = package.Manifest.Version,
                    ReleaseHash = package.ReleaseHash,
                    PackageHash = package.PackageHash,
                    Manifest = document.RootElement.Clone(),
                };
            })
            .ToArray();
        var revision = CreateRevision(entries);
        return (revision, afterRevision == revision ? [] : entries);
    }

    public async Task<Stream> GetDesktopPackageAsync(
        Guid extensionId,
        string releaseHash,
        CancellationToken cancellationToken
    )
    {
        var package = GetValidPackage(extensionId);
        if (
            !package.Manifest.Targets.HasFlag(ExtensionTarget.Desktop)
            || !string.Equals(package.ReleaseHash, releaseHash, StringComparison.Ordinal)
        )
            throw new AgwException(ErrorCodes.ExtensionNotFound);
        var stream = new FileStream(
            package.ArchivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        try
        {
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)) != package.PackageHash)
                throw new AgwException(ErrorCodes.ExtensionPackageCorrupted);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private async Task<ExtensionLocalPackage> ReadChildAsync(string childDirectory, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new AgwException(
                ErrorCodes.ExtensionPackageInvalid,
                "Extension directories cannot be symbolic links."
            );

        var files = await ReadFilesAsync(childDirectory, cancellationToken);
        var fileBytes = files.Values.Sum(contents => contents.LongLength);
        if (fileBytes > _options.MaximumPackageBytes)
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension package exceeds the size limit.");

        var package = ExtensionPackageReader.ValidateFiles(files);
        var manifest = package.Manifest;
        var (snapshotDirectory, _) = await _packages.MaterializeAsync(package, cancellationToken);
        var desktopBytes =
            manifest.Targets == (ExtensionTarget.Server | ExtensionTarget.Desktop)
                ? CreateArchive(
                    files
                        .Where(file =>
                            file.Key == "manifest.json" || file.Key.StartsWith("desktop/", StringComparison.Ordinal)
                        )
                        .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal)
                )
                : CreateArchive(files);
        var desktopPackageHash = Convert.ToHexStringLower(SHA256.HashData(desktopBytes));
        var archivePath = await _packages.CacheArchiveAsync(
            package.ReleaseHash,
            desktopPackageHash,
            desktopBytes,
            cancellationToken
        );
        return new ExtensionLocalPackage
        {
            ExtensionId = Guid.Parse(manifest.Id),
            Manifest = package.Manifest,
            ManifestJson = package.ManifestJson,
            ReleaseHash = package.ReleaseHash,
            PackageHash = desktopPackageHash,
            SnapshotDirectory = snapshotDirectory,
            ArchivePath = archivePath,
        };
    }

    private async Task<Dictionary<string, byte[]>> ReadFilesAsync(string root, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await ReadFilesAsync(root, root, files, 0, cancellationToken);
        return files;
    }

    private async Task<long> ReadFilesAsync(
        string root,
        string directory,
        Dictionary<string, byte[]> files,
        long totalBytes,
        CancellationToken cancellationToken
    )
    {
        foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension files cannot be symbolic links.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                totalBytes = await ReadFilesAsync(root, path, files, totalBytes, cancellationToken);
                continue;
            }

            var relativePath = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            ExtensionManifestReader.ValidatePath(relativePath);
            var length = new FileInfo(path).Length;
            if (length > _options.MaximumExtractedBytes - totalBytes)
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension files exceed the size limit.");
            var contents = await File.ReadAllBytesAsync(path, cancellationToken);
            if (contents.LongLength > _options.MaximumExtractedBytes - totalBytes)
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension files exceed the size limit.");
            totalBytes += contents.LongLength;
            if (!files.TryAdd(relativePath, contents))
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension contains duplicate file names.");
        }
        return totalBytes;
    }

    private static byte[] CreateArchive(IReadOnlyDictionary<string, byte[]> files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, contents) in files.OrderBy(file => file.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(contents);
            }
        }
        return stream.ToArray();
    }

    private static long CreateRevision(IReadOnlyList<ExtensionCatalogEntry> entries)
    {
        var contents = string.Join(
            "\n",
            entries.Select(entry => $"{entry.Id}\0{entry.ReleaseHash}\0{entry.PackageHash}")
        );
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(contents));
        var revision = (long)(BinaryPrimitives.ReadUInt64LittleEndian(digest) & 0x1FFFFFFFFFFFFFUL);
        return revision == 0 ? 1 : revision;
    }
}
