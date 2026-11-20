using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Packages;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agw.Extensions.Tests;

public sealed class ExtensionPackageCatalogTests
{
    [Fact]
    public async Task GetDesktopPackageAsync_ValidatedSnapshot_StreamsOnlyDesktopResources()
    {
        var root = CreateTestDirectory();
        try
        {
            var extensionId = Guid.CreateVersion7();
            var manifest = await WriteExtensionAsync(root, "desktop", extensionId);
            var catalog = CreateCatalog(root);
            await catalog.InitializeAsync(TestContext.Current.CancellationToken);
            var entry = Assert.Single(catalog.GetEntries());

            await using var stream = Assert.IsAssignableFrom<Stream>(
                await catalog.GetDesktopPackageAsync(
                    extensionId,
                    entry.ReleaseHash,
                    TestContext.Current.CancellationToken
                )
            );

            Assert.IsType<FileStream>(stream);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            Assert.Equal(
                new[] { "desktop/index.html", "manifest.json" },
                archive.Entries.Select(item => item.FullName).Order()
            );
            using var manifestStream = archive.GetEntry("manifest.json")!.Open();
            using var contents = new MemoryStream();
            await manifestStream.CopyToAsync(contents, TestContext.Current.CancellationToken);
            Assert.Equal(manifest, contents.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_PayloadAtPackageLimit_LoadsSnapshot()
    {
        var root = CreateTestDirectory();
        try
        {
            var manifest = await WriteExtensionAsync(root, "limit", Guid.CreateVersion7());
            var limit = manifest.Length + "entry"u8.Length + "<html>Desktop</html>"u8.Length;
            var catalog = CreateCatalog(
                root,
                new ExtensionOptions { MaximumPackageBytes = limit, MaximumExtractedBytes = limit }
            );

            await catalog.InitializeAsync(TestContext.Current.CancellationToken);

            Assert.Single(catalog.GetEntries());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_UsesContentRootAndKeepsOneSnapshot()
    {
        var root = CreateTestDirectory();
        try
        {
            var firstId = Guid.CreateVersion7();
            var manifestBytes = await WriteExtensionAsync(root, "first", firstId);
            var catalog = CreateCatalog(root);
            await catalog.InitializeAsync(TestContext.Current.CancellationToken);

            var entry = Assert.Single(catalog.GetEntries());
            Assert.Equal(firstId, entry.ExtensionId);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), entry.ReleaseHash);

            Assert.True(File.Exists(entry.ArchivePath));
            Assert.True(Directory.Exists(entry.SnapshotDirectory));
            var snapshotContentPath = Path.Combine(entry.SnapshotDirectory, "server", "entry.dll");
            await File.WriteAllTextAsync(
                Path.Combine(root, "Extensions", "first", "server", "entry.dll"),
                "changed",
                TestContext.Current.CancellationToken
            );
            await using (
                var archive = await catalog.GetDesktopPackageAsync(
                    firstId,
                    entry.ReleaseHash,
                    TestContext.Current.CancellationToken
                )
            )
                Assert.Equal(
                    Convert.ToHexStringLower(
                        await SHA256.HashDataAsync(archive, TestContext.Current.CancellationToken)
                    ),
                    entry.PackageHash
                );
            Assert.Equal(
                "entry",
                await File.ReadAllTextAsync(snapshotContentPath, TestContext.Current.CancellationToken)
            );
            var (revision, entries) = catalog.GetDesktopCatalog(null, new HashSet<Guid> { firstId });
            Assert.InRange(revision, 1, 9_007_199_254_740_991);
            Assert.Equal(entry.PackageHash, Assert.Single(entries).PackageHash);
            var (sameRevision, unchanged) = catalog.GetDesktopCatalog(revision, new HashSet<Guid> { firstId });
            Assert.Equal(revision, sameRevision);
            Assert.Empty(unchanged);

            await File.WriteAllBytesAsync(entry.ArchivePath, [1, 2, 3], TestContext.Current.CancellationToken);
            var cacheError = await Assert.ThrowsAsync<AgwException>(() =>
                catalog.GetDesktopPackageAsync(firstId, entry.ReleaseHash, TestContext.Current.CancellationToken)
            );
            Assert.Equal(ErrorCodes.ExtensionPackageCorrupted.Code, cacheError.Code);

            await WriteExtensionAsync(root, "added-later", Guid.CreateVersion7());
            await catalog.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Single(catalog.GetEntries());
            Assert.Equal(revision, catalog.GetDesktopCatalog(null, new HashSet<Guid> { firstId }).Revision);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_CombinedPackageReferencesServerResourceFromDesktop_RejectsPackage()
    {
        var root = CreateTestDirectory();
        try
        {
            await WriteExtensionAsync(root, "desktop-path", Guid.CreateVersion7(), desktopEntry: "server/entry.dll");
            var catalog = CreateCatalog(root);

            var error = await Assert.ThrowsAsync<AgwException>(() =>
                catalog.InitializeAsync(TestContext.Current.CancellationToken)
            );

            Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, error.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_InvalidChecksum_FailsStartup()
    {
        var root = CreateTestDirectory();
        try
        {
            var checksumId = Guid.CreateVersion7();
            await WriteExtensionAsync(root, "checksum", checksumId, expectedContent: "other");

            var catalog = CreateCatalog(root);
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                catalog.InitializeAsync(TestContext.Current.CancellationToken)
            );
            Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, error.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_InvalidPath_FailsStartup()
    {
        var root = CreateTestDirectory();
        try
        {
            await WriteExtensionAsync(root, "path", Guid.CreateVersion7(), invalidPath: true);

            var catalog = CreateCatalog(root);
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                catalog.InitializeAsync(TestContext.Current.CancellationToken)
            );
            Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, error.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_DuplicateIds_RejectsCatalog()
    {
        var root = CreateTestDirectory();
        try
        {
            var extensionId = Guid.CreateVersion7();
            await WriteExtensionAsync(root, "first", extensionId);
            await WriteExtensionAsync(root, "second", extensionId);

            var catalog = CreateCatalog(root);
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                catalog.InitializeAsync(TestContext.Current.CancellationToken)
            );
            Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, error.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetDesktopCatalog_ActiveExtensionSet_FiltersIndependentPackages()
    {
        var root = CreateTestDirectory();
        try
        {
            var firstId = Guid.CreateVersion7();
            var secondId = Guid.CreateVersion7();
            await WriteExtensionAsync(root, "first", firstId);
            await WriteExtensionAsync(root, "second", secondId);

            var catalog = CreateCatalog(root);
            await catalog.InitializeAsync(TestContext.Current.CancellationToken);

            var (revision, entries) = catalog.GetDesktopCatalog(null, new HashSet<Guid> { secondId });
            Assert.InRange(revision, 1, 9_007_199_254_740_991);
            Assert.Equal(secondId.ToString(), Assert.Single(entries).Id);
            Assert.Equal(revision, catalog.GetDesktopCatalog(revision, new HashSet<Guid> { secondId }).Revision);
            var (firstRevision, firstEntries) = catalog.GetDesktopCatalog(null, new HashSet<Guid> { firstId });
            Assert.NotEqual(revision, firstRevision);
            Assert.Equal(firstId.ToString(), Assert.Single(firstEntries).Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_DuplicateTechnicalNames_RejectsCatalog()
    {
        var root = CreateTestDirectory();
        try
        {
            await WriteExtensionAsync(root, "first", Guid.CreateVersion7(), name: "duplicate");
            await WriteExtensionAsync(root, "second", Guid.CreateVersion7(), name: "duplicate");

            var catalog = CreateCatalog(root);
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                catalog.InitializeAsync(TestContext.Current.CancellationToken)
            );
            Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, error.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_MissingRoot_ReturnsEmptyWithoutCreatingDirectory()
    {
        var root = CreateTestDirectory();
        try
        {
            var catalog = CreateCatalog(root);
            await catalog.InitializeAsync(TestContext.Current.CancellationToken);

            Assert.Empty(catalog.GetEntries());
            Assert.False(Directory.Exists(Path.Combine(root, "Extensions")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ExtensionPackageCatalog CreateCatalog(string contentRoot, ExtensionOptions? extensionOptions = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { ContentRootPath = contentRoot }
        );
        var options = Options.Create(extensionOptions ?? new ExtensionOptions());
        var cache = new ExtensionPackageCache(AgwDataPaths.Resolve(contentRoot, contentRoot));
        return new ExtensionPackageCatalog(builder.Environment, cache, options);
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "extension-catalog-tests",
            Guid.CreateVersion7().ToString("N")
        );
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<byte[]> WriteExtensionAsync(
        string contentRoot,
        string directoryName,
        Guid extensionId,
        string? name = null,
        string content = "entry",
        string? expectedContent = null,
        bool invalidPath = false,
        string desktopEntry = "desktop/index.html"
    )
    {
        var extensionDirectory = Path.Combine(contentRoot, "Extensions", directoryName);
        var entryPath = Path.Combine(extensionDirectory, "server", "entry.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
        var contentBytes = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(entryPath, contentBytes, TestContext.Current.CancellationToken);

        var expectedBytes = Encoding.UTF8.GetBytes(expectedContent ?? content);
        var desktopBytes = "<html>Desktop</html>"u8.ToArray();
        Directory.CreateDirectory(Path.Combine(extensionDirectory, "desktop"));
        await File.WriteAllBytesAsync(
            Path.Combine(extensionDirectory, "desktop", "index.html"),
            desktopBytes,
            TestContext.Current.CancellationToken
        );
        var files = new Dictionary<string, string>
        {
            ["server/entry.dll"] = Convert.ToHexStringLower(SHA256.HashData(expectedBytes)),
            ["desktop/index.html"] = Convert.ToHexStringLower(SHA256.HashData(desktopBytes)),
        };
        if (invalidPath)
            files["../outside.dll"] = Convert.ToHexStringLower(SHA256.HashData("outside"u8));
        var manifest = JsonSerializer.Serialize(
            new
            {
                manifestVersion = 2,
                id = extensionId.ToString(),
                name = name ?? $"extension-{extensionId:N}",
                displayName = "Extension",
                version = "1.0.0",
                targets = (int)(ExtensionTarget.Server | ExtensionTarget.Desktop),
                server = new
                {
                    apiVersion = 1,
                    entryAssembly = "server/entry.dll",
                    entryType = "Extension.Entry",
                },
                desktop = new
                {
                    apiVersion = 1,
                    contributes = new
                    {
                        mainViews = new[]
                        {
                            new
                            {
                                id = "main",
                                title = "Main",
                                icon = "Box",
                                entry = desktopEntry,
                            },
                        },
                    },
                },
                files,
            }
        );
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        await File.WriteAllBytesAsync(
            Path.Combine(extensionDirectory, "manifest.json"),
            manifestBytes,
            TestContext.Current.CancellationToken
        );
        return manifestBytes;
    }
}
