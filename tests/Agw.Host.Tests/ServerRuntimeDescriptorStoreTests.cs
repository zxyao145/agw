using System.Text.Json;

using Agw.Host.Runtime;
using Agw.Shared.Runtime;

using Xunit;

namespace Agw.Host.Tests;

public class ServerRuntimeDescriptorStoreTests
{
    [Fact]
    public async Task WriteAsync_ValidDescriptor_WritesVersionedJson()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agw-runtime-{Guid.CreateVersion7():N}");
        var paths = AgwDataPaths.Resolve(root, "/unused");
        paths.EnsureCreated();
        var store = new ServerRuntimeDescriptorStore(paths);
        var startedAt = new DateTimeOffset(2026, 7, 17, 1, 2, 3, TimeSpan.Zero);

        try
        {
            await store.WriteAsync(
                new ServerRuntimeDescriptor(
                    SchemaVersion: 1,
                    Pid: 1234,
                    BaseUrl: "http://127.0.0.1:30815",
                    Port: 30815,
                    ServerVersion: "1.2.3",
                    ApiMajorVersion: 1,
                    StartedAt: startedAt),
                TestContext.Current.CancellationToken);

            var json = await File.ReadAllTextAsync(
                paths.ServerRuntimeFile,
                TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(json);
            var rootElement = document.RootElement;
            Assert.Equal(1, rootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(1234, rootElement.GetProperty("pid").GetInt32());
            Assert.Equal("http://127.0.0.1:30815", rootElement.GetProperty("baseUrl").GetString());
            Assert.Equal("2026-07-17T01:02:03+00:00", rootElement.GetProperty("startedAt").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteIfOwnedAsync_DescriptorBelongsToAnotherProcess_PreservesFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agw-runtime-{Guid.CreateVersion7():N}");
        var paths = AgwDataPaths.Resolve(root, "/unused");
        paths.EnsureCreated();
        var store = new ServerRuntimeDescriptorStore(paths);

        try
        {
            await store.WriteAsync(
                new ServerRuntimeDescriptor(1, 4321, "http://127.0.0.1:30815", 30815, "1.2.3", 1, new DateTimeOffset(2026, 7, 17, 1, 2, 3, TimeSpan.Zero)),
                TestContext.Current.CancellationToken);

            await store.DeleteIfOwnedAsync(1234, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(paths.ServerRuntimeFile));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteIfOwnedAsync_DescriptorBelongsToProcess_DeletesFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agw-runtime-{Guid.CreateVersion7():N}");
        var paths = AgwDataPaths.Resolve(root, "/unused");
        paths.EnsureCreated();
        var store = new ServerRuntimeDescriptorStore(paths);

        try
        {
            await store.WriteAsync(
                new ServerRuntimeDescriptor(1, 1234, "http://127.0.0.1:30815", 30815, "1.2.3", 1, new DateTimeOffset(2026, 7, 17, 1, 2, 3, TimeSpan.Zero)),
                TestContext.Current.CancellationToken);

            await store.DeleteIfOwnedAsync(1234, TestContext.Current.CancellationToken);

            Assert.False(File.Exists(paths.ServerRuntimeFile));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
