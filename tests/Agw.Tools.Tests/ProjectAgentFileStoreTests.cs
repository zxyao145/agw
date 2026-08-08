using Agw.Files.Abstracts;
using Agw.Files.Application.Storage.Local;
using Agw.Shared.Exceptions;
using Agw.Tools.ToolBlocks.Storage;

namespace Agw.Tools.Tests;

public sealed class ProjectAgentFileStoreTests
{
    [Fact]
    public async Task ScopedStore_WritesBelowSharedProjectMemoryDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workspace = Path.Combine(
            Path.GetTempPath(),
            $"agw-project-memory-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var fileSystem = new LocalFileSystem(workspace);
            var store = new ProjectAgentFileStore(
                new StubFileSystemResolver(fileSystem),
                Guid.CreateVersion7(),
                ".agw/memory");

            await store.WriteAsync(
                "notes.md",
                "project memory",
                cancellationToken);

            Assert.Equal(
                "project memory",
                await store.ReadAsync("notes.md", cancellationToken));
            Assert.True(File.Exists(Path.Combine(
                workspace,
                ".agw",
                "memory",
                "notes.md")));
            Assert.False(File.Exists(Path.Combine(workspace, "notes.md")));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScopedStore_SameWorkspaceSharesMemoryAcrossProjects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workspace = Path.Combine(
            Path.GetTempPath(),
            $"agw-project-memory-shared-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var resolver = new StubFileSystemResolver(new LocalFileSystem(workspace));
            var firstStore = new ProjectAgentFileStore(
                resolver,
                Guid.CreateVersion7(),
                ".agw/memory");
            var secondStore = new ProjectAgentFileStore(
                resolver,
                Guid.CreateVersion7(),
                ".agw/memory");

            await firstStore.WriteAsync("notes.md", "shared", cancellationToken);

            Assert.Equal(
                "shared",
                await secondStore.ReadAsync("notes.md", cancellationToken));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ScopedStore_PathTraversal_IsRejected()
    {
        var fileSystem = new LocalFileSystem(Path.GetTempPath());
        var store = new ProjectAgentFileStore(
            new StubFileSystemResolver(fileSystem),
            Guid.CreateVersion7(),
            ".agw/memory");

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            store.WriteAsync(
                "../outside.md",
                "invalid",
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    private sealed class StubFileSystemResolver : IAgwFileSystemResolver
    {
        private readonly IAgwFileSystem _fileSystem;

        public StubFileSystemResolver(IAgwFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }

        public Task<IAgwFileSystem> ResolveAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult(_fileSystem);
    }
}
