using System.Security.Cryptography;
using Agw.Extensions.Contracts;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;

namespace Agw.Extensions.Infrastructure.Packages;

public sealed class ExtensionPackageCache
{
    private readonly AgwDataPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExtensionPackageCache(AgwDataPaths paths)
    {
        _paths = paths;
    }

    public async Task<(string Directory, ExtensionManifest Manifest)> MaterializeAsync(
        ValidatedExtensionPackage package,
        CancellationToken cancellationToken
    )
    {
        var releaseHash = package.ReleaseHash;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(_paths.Root, "extensions", "releases", releaseHash);
            if (Directory.Exists(directory))
            {
                await ValidateCachedDirectoryAsync(directory, package.Files, cancellationToken);
                return (directory, package.Manifest);
            }
            var stage = Path.Combine(_paths.Root, "extensions", "staging", Guid.CreateVersion7().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                foreach (var (relativePath, contents) in package.Files)
                {
                    var path = Path.Combine(stage, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, contents, cancellationToken);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
                try
                {
                    Directory.Move(stage, directory);
                }
                catch (IOException) when (Directory.Exists(directory))
                {
                    await ValidateCachedDirectoryAsync(directory, package.Files, cancellationToken);
                }
                return (directory, package.Manifest);
            }
            finally
            {
                if (Directory.Exists(stage))
                    Directory.Delete(stage, recursive: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> CacheArchiveAsync(
        string releaseHash,
        string packageHash,
        byte[] bytes,
        CancellationToken cancellationToken
    )
    {
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != packageHash)
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension package digest does not match.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var archivePath = Path.Combine(_paths.Root, "extensions", "packages", $"{releaseHash}.zip");
            if (File.Exists(archivePath))
            {
                await ValidateCachedArchiveAsync(archivePath, packageHash, bytes, cancellationToken);
                return archivePath;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            var stage = archivePath + $".{Guid.CreateVersion7():N}.new";
            try
            {
                await File.WriteAllBytesAsync(stage, bytes, cancellationToken);
                try
                {
                    File.Move(stage, archivePath);
                }
                catch (IOException) when (File.Exists(archivePath))
                {
                    await ValidateCachedArchiveAsync(archivePath, packageHash, bytes, cancellationToken);
                }
                return archivePath;
            }
            finally
            {
                if (File.Exists(stage))
                    File.Delete(stage);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task ValidateCachedDirectoryAsync(
        string directory,
        IReadOnlyDictionary<string, byte[]> files,
        CancellationToken cancellationToken
    )
    {
        foreach (var (relativePath, contents) in files)
        {
            var path = Path.Combine(directory, relativePath);
            var cached = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
            if (new FileInfo(path).LinkTarget is not null || cached is null || !contents.AsSpan().SequenceEqual(cached))
                throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Cached extension package has changed.");
        }
    }

    private static async Task ValidateCachedArchiveAsync(
        string archivePath,
        string packageHash,
        byte[] bytes,
        CancellationToken cancellationToken
    )
    {
        var cached = await File.ReadAllBytesAsync(archivePath, cancellationToken);
        if (Convert.ToHexStringLower(SHA256.HashData(cached)) != packageHash || !cached.AsSpan().SequenceEqual(bytes))
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Cached extension archive has changed.");
    }
}
