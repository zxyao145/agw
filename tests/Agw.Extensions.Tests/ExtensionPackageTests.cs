using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Extensions.Infrastructure.Packages;
using Agw.Shared.Exceptions;
using Xunit;

namespace Agw.Extensions.Tests;

public sealed class ExtensionPackageTests
{
    [Fact]
    public async Task ValidateAsync_ValidPackage_VerifiesAllFilesAndReleaseHash()
    {
        var bytes = CreatePackage("server/entry.dll", "content", "content");
        var package = await ExtensionPackageReader.ValidateAsync(
            bytes,
            new ExtensionOptions(),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(package.Files["manifest.json"])), package.ReleaseHash);
        Assert.Equal("calendar", package.Manifest.Name);
        Assert.Equal("content", Encoding.UTF8.GetString(package.Files["server/entry.dll"]));
    }

    [Fact]
    public async Task ValidateAsync_RepackedContent_PreservesReleaseHash()
    {
        var original = CreatePackage("server/entry.dll", "content", "content");
        using var repacked = new MemoryStream();
        repacked.Write(original);
        using (var archive = new ZipArchive(repacked, ZipArchiveMode.Update, leaveOpen: true))
            archive.GetEntry("manifest.json")!.LastWriteTime = new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
        var first = await ExtensionPackageReader.ValidateAsync(
            original,
            new ExtensionOptions(),
            TestContext.Current.CancellationToken
        );
        var second = await ExtensionPackageReader.ValidateAsync(
            repacked.ToArray(),
            new ExtensionOptions(),
            TestContext.Current.CancellationToken
        );
        Assert.NotEqual(
            Convert.ToHexStringLower(SHA256.HashData(original)),
            Convert.ToHexStringLower(SHA256.HashData(repacked.ToArray()))
        );
        Assert.Equal(first.ReleaseHash, second.ReleaseHash);
    }

    [Fact]
    public async Task ValidateAsync_LeavesHostCompatibilityToTheExtension()
    {
        var package = await ExtensionPackageReader.ValidateAsync(
            CreatePackage("server/entry.dll", "content", "content", apiVersion: 2, minHostVersion: "99.0.0"),
            new ExtensionOptions(),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(2, package.Manifest.Server!.ApiVersion);
        Assert.Equal("99.0.0", package.Manifest.Server.MinHostVersion);
    }

    [Fact]
    public async Task ValidateAsync_ChecksumMismatch_RejectsPackage()
    {
        await Assert.ThrowsAsync<AgwException>(() =>
            ExtensionPackageReader.ValidateAsync(
                CreatePackage("server/entry.dll", "changed", "content"),
                new ExtensionOptions(),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task ValidateAsync_Traversal_RejectsPackage()
    {
        await Assert.ThrowsAsync<AgwException>(() =>
            ExtensionPackageReader.ValidateAsync(
                CreatePackage("../entry.dll", "content", "content"),
                new ExtensionOptions(),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task ValidateAsync_UnlistedFile_RejectsPackage()
    {
        await Assert.ThrowsAsync<AgwException>(() =>
            ExtensionPackageReader.ValidateAsync(
                CreatePackage("server/entry.dll", "content", "content", extra: true),
                new ExtensionOptions(),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task ValidateAsync_ExtractionLimit_RejectsPackage()
    {
        await Assert.ThrowsAsync<AgwException>(() =>
            ExtensionPackageReader.ValidateAsync(
                CreatePackage("server/entry.dll", "content", "content"),
                new ExtensionOptions { MaximumExtractedBytes = 1 },
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task ValidateAsync_FileDirectoryCollision_RejectsPackage()
    {
        var bytes = CreatePackage("server/entry.dll", "content", "content", conflictingDirectory: true);
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            ExtensionPackageReader.ValidateAsync(bytes, new ExtensionOptions(), TestContext.Current.CancellationToken)
        );
        Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, error.Code);
    }

    internal static byte[] CreatePackage(
        string path,
        string content,
        string expectedContent,
        bool extra = false,
        bool conflictingDirectory = false,
        int apiVersion = 1,
        string? minHostVersion = null
    )
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var files = new Dictionary<string, string>
            {
                [path] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expectedContent))),
            };
            if (conflictingDirectory)
                files["server"] = Convert.ToHexStringLower(SHA256.HashData("collision"u8));
            var manifest = JsonSerializer.Serialize(
                new
                {
                    manifestVersion = 2,
                    id = "019a4c17-5f00-7a80-8b3d-2b732f48a100",
                    name = "calendar",
                    displayName = "Calendar",
                    version = "1.0.0",
                    targets = 1,
                    server = new
                    {
                        apiVersion,
                        minHostVersion,
                        entryAssembly = path,
                        entryType = "Calendar.Entry",
                    },
                    files,
                }
            );
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                writer.Write(manifest);
            using (var writer = new StreamWriter(zip.CreateEntry(path).Open()))
                writer.Write(content);
            if (conflictingDirectory)
                using (var writer = new StreamWriter(zip.CreateEntry("server").Open()))
                    writer.Write("collision");
            if (extra)
                using (var writer = new StreamWriter(zip.CreateEntry("unlisted.txt").Open()))
                    writer.Write("unlisted");
        }
        return stream.ToArray();
    }
}
