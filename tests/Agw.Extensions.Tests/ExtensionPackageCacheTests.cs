using System.Security.Cryptography;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Packages;
using Agw.Shared.Runtime;
using Xunit;

namespace Agw.Extensions.Tests;

public sealed class ExtensionPackageCacheTests
{
    [Fact]
    public async Task CacheAsync_TwoInstancesSharingRoot_ReuseConcurrentWinners()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "extension-cache-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var content = new string('x', 4 * 1024 * 1024);
            var bytes = ExtensionPackageTests.CreatePackage("server/entry.dll", content, content);
            var package = await ExtensionPackageReader.ValidateAsync(
                bytes,
                new ExtensionOptions(),
                TestContext.Current.CancellationToken
            );
            var paths = AgwDataPaths.Resolve(root, root);
            var firstCache = new ExtensionPackageCache(paths);
            var secondCache = new ExtensionPackageCache(paths);

            var materializationStart = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var firstMaterialization = MaterializeAsync(firstCache);
            var secondMaterialization = MaterializeAsync(secondCache);
            materializationStart.SetResult(true);
            var materialized = await Task.WhenAll(firstMaterialization, secondMaterialization);
            var expectedDirectory = Path.Combine(paths.Root, "extensions", "releases", package.ReleaseHash);
            Assert.All(materialized, result => Assert.Equal(expectedDirectory, result.Directory));
            Assert.Equal(
                content,
                await File.ReadAllTextAsync(
                    Path.Combine(expectedDirectory, "server", "entry.dll"),
                    TestContext.Current.CancellationToken
                )
            );

            var archiveStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstArchive = CacheArchiveAsync(firstCache);
            var secondArchive = CacheArchiveAsync(secondCache);
            archiveStart.SetResult(true);
            var archives = await Task.WhenAll(firstArchive, secondArchive);
            var expectedArchive = Path.Combine(paths.Root, "extensions", "packages", $"{package.ReleaseHash}.zip");
            Assert.All(archives, archivePath => Assert.Equal(expectedArchive, archivePath));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(expectedArchive, TestContext.Current.CancellationToken));

            async Task<(string Directory, ExtensionManifest Manifest)> MaterializeAsync(ExtensionPackageCache cache)
            {
                await materializationStart.Task;
                return await cache.MaterializeAsync(package, TestContext.Current.CancellationToken);
            }

            async Task<string> CacheArchiveAsync(ExtensionPackageCache cache)
            {
                await archiveStart.Task;
                return await cache.CacheArchiveAsync(
                    package.ReleaseHash,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    bytes,
                    TestContext.Current.CancellationToken
                );
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
