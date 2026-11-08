using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Agw.Extensions.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Extensions.Infrastructure.Packages;

public sealed record ValidatedExtensionPackage
{
    public required ExtensionManifest Manifest { get; init; }
    public required string ManifestJson { get; init; }
    public required string ReleaseHash { get; init; }
    public required IReadOnlyDictionary<string, byte[]> Files { get; init; }
}

public static class ExtensionPackageReader
{
    public static async Task<ValidatedExtensionPackage> ValidateAsync(
        byte[] bytes,
        ExtensionOptions options,
        CancellationToken cancellationToken = default
    )
    {
        if (bytes.Length == 0 || bytes.Length > options.MaximumPackageBytes)
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension package size is invalid.");
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long remaining = options.MaximumExtractedBytes;
            foreach (var entry in archive.Entries)
            {
                var directory = entry.FullName.EndsWith('/');
                ExtensionManifestReader.ValidatePath(directory ? entry.FullName[..^1] : entry.FullName);
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new AgwException(
                        ErrorCodes.ExtensionPackageInvalid,
                        "Symbolic links are not supported in extension packages."
                    );
                if (directory)
                {
                    directories.Add(entry.FullName[..^1]);
                    continue;
                }
                await using var contents = entry.Open();
                var data = await ReadBoundedAsync(contents, remaining, cancellationToken);
                remaining -= data.Length;
                if (!names.Add(entry.FullName) || !files.TryAdd(entry.FullName, data))
                    throw new AgwException(
                        ErrorCodes.ExtensionPackageInvalid,
                        "Package contains duplicate file names."
                    );
            }
            if (names.Overlaps(directories))
                throw new AgwException(
                    ErrorCodes.ExtensionPackageInvalid,
                    "Package file and directory paths conflict."
                );
            return ValidateFiles(files);
        }
        catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException)
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension archive could not be read.");
        }
    }

    public static ValidatedExtensionPackage ValidateFiles(IReadOnlyDictionary<string, byte[]> files)
    {
        var names = new HashSet<string>(files.Keys, StringComparer.OrdinalIgnoreCase);
        if (names.Count != files.Count)
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Package contains duplicate file names.");
        foreach (var path in files.Keys)
        {
            ExtensionManifestReader.ValidatePath(path);
            for (
                var parent = Path.GetDirectoryName(path);
                !string.IsNullOrEmpty(parent);
                parent = Path.GetDirectoryName(parent)
            )
                if (names.Contains(parent.Replace('\\', '/')))
                    throw new AgwException(
                        ErrorCodes.ExtensionPackageInvalid,
                        "Package file and directory paths conflict."
                    );
        }
        if (!files.TryGetValue("manifest.json", out var manifestBytes))
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Package must contain manifest.json.");
        var manifestJson = new UTF8Encoding(false, true).GetString(manifestBytes);
        var manifest = ExtensionManifestReader.Parse(manifestJson);
        if (!manifest.Targets.HasFlag(ExtensionTarget.Server) || manifest.Server is null)
            throw new AgwException(
                ErrorCodes.ExtensionPackageInvalid,
                "A Server extension package requires the Server target."
            );
        if (files.Count != manifest.Files.Count + 1)
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "All package files must have checksums.");
        foreach (var (path, expected) in manifest.Files)
        {
            if (
                !files.TryGetValue(path, out var content)
                || Convert.ToHexStringLower(SHA256.HashData(content)) != expected
            )
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Package file checksum does not match.");
        }
        if (!files.ContainsKey(manifest.Server!.EntryAssembly))
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Server entry assembly is missing.");
        return new ValidatedExtensionPackage
        {
            Manifest = manifest,
            ManifestJson = manifestJson,
            ReleaseHash = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
            Files = files,
        };
    }

    public static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken
    )
    {
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (destination.Length + count > maximumBytes)
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension package exceeds the size limit.");
            destination.Write(buffer, 0, count);
        }
        return destination.ToArray();
    }
}
