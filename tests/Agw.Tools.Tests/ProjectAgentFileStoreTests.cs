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
        var workspace = Path.Combine(Path.GetTempPath(), $"agw-project-memory-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var fileSystem = new LocalFileSystem(workspace);
            var store = new ProjectAgentFileStore(
                new StubFileSystemResolver(fileSystem),
                Guid.CreateVersion7(),
                ".agw/memory"
            );

            await store.WriteAsync("notes.md", "project memory", cancellationToken);

            Assert.Equal("project memory", await store.ReadAsync("notes.md", cancellationToken));
            Assert.True(File.Exists(Path.Combine(workspace, ".agw", "memory", "notes.md")));
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
        var workspace = Path.Combine(Path.GetTempPath(), $"agw-project-memory-shared-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var resolver = new StubFileSystemResolver(new LocalFileSystem(workspace));
            var firstStore = new ProjectAgentFileStore(resolver, Guid.CreateVersion7(), ".agw/memory");
            var secondStore = new ProjectAgentFileStore(resolver, Guid.CreateVersion7(), ".agw/memory");

            await firstStore.WriteAsync("notes.md", "shared", cancellationToken);

            Assert.Equal("shared", await secondStore.ReadAsync("notes.md", cancellationToken));
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
            ".agw/memory"
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            store.WriteAsync("../outside.md", "invalid", TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task ReadAsync_FileExceedsLimit_IsRejected()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"agw-file-read-limit-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            await using (var file = File.Create(Path.Combine(workspace, "large.txt")))
            {
                file.SetLength((128 * 1024) + 1);
            }

            var store = CreateStore(workspace);

            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                store.ReadAsync("large.txt", TestContext.Current.CancellationToken)
            );

            Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
            Assert.Contains("128 KiB", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ListChildrenAsync_DirectoryExceedsLimit_IsRejected()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"agw-file-list-limit-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            for (var i = 0; i < 1_001; i++)
            {
                File.Create(Path.Combine(workspace, $"file-{i:D4}.txt")).Dispose();
            }

            var store = CreateStore(workspace);

            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                store.ListChildrenAsync(string.Empty, TestContext.Current.CancellationToken)
            );

            Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_Recursive_SkipsGeneratedDirectories()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workspace = Path.Combine(Path.GetTempPath(), $"agw-file-search-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var includedDirectory = Directory.CreateDirectory(Path.Combine(workspace, "src"));
            await File.WriteAllTextAsync(
                Path.Combine(includedDirectory.FullName, "included.cs"),
                "needle",
                cancellationToken
            );

            string[] generatedDirectories =
            [
                ".git",
                ".worktrees",
                "node_modules",
                "bin",
                "obj",
                ".next",
                ".turbo",
                "dist",
            ];
            foreach (var directoryName in generatedDirectories)
            {
                var directory = Directory.CreateDirectory(Path.Combine(workspace, directoryName));
                await File.WriteAllTextAsync(
                    Path.Combine(directory.FullName, "ignored.txt"),
                    "needle",
                    cancellationToken
                );
            }

            var store = CreateStore(workspace);

            var results = await store.SearchAsync(
                string.Empty,
                "needle",
                recursive: true,
                cancellationToken: cancellationToken
            );

            var result = Assert.Single(results);
            Assert.Equal("src/included.cs", result.FileName);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_Recursive_LimitsMatchingLines()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workspace = Path.Combine(Path.GetTempPath(), $"agw-file-search-limit-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(workspace, "matches.txt"),
                Enumerable.Repeat("needle", 250),
                cancellationToken
            );
            var store = CreateStore(workspace);

            var results = await store.SearchAsync(
                string.Empty,
                "needle",
                recursive: true,
                cancellationToken: cancellationToken
            );

            var result = Assert.Single(results);
            Assert.Equal(200, result.MatchingLines.Count);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_MatchingLineExceedsLimit_TruncatesLine()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workspace = Path.Combine(Path.GetTempPath(), $"agw-file-search-line-limit-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "minified.js"),
                "needle" + new string('x', 10_000),
                cancellationToken
            );
            var store = CreateStore(workspace);

            var results = await store.SearchAsync(
                string.Empty,
                "needle",
                recursive: true,
                cancellationToken: cancellationToken
            );

            var match = Assert.Single(Assert.Single(results).MatchingLines);
            Assert.Equal(4 * 1024, match.Line.Length);
            Assert.EndsWith("... [truncated]", match.Line, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_ResultExceedsLimit_StopsAddingMatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workspace = Path.Combine(Path.GetTempPath(), $"agw-file-search-result-limit-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(workspace, "matches.txt"),
                Enumerable.Repeat("needle" + new string('x', 5_000), 200),
                cancellationToken
            );
            var store = CreateStore(workspace);

            var results = await store.SearchAsync(
                string.Empty,
                "needle",
                recursive: true,
                cancellationToken: cancellationToken
            );

            var result = Assert.Single(results);
            Assert.InRange(result.MatchingLines.Count, 1, 199);
            var resultCharacters =
                result.FileName.Length
                + result.Snippet.Length
                + result.MatchingLines.Sum(static match => match.Line.Length);
            Assert.InRange(resultCharacters, 1, 64 * 1024);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_GlobPattern_MatchesRelativePaths()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workspace = Path.Combine(Path.GetTempPath(), $"agw-file-search-glob-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(workspace, "src"));
            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory.FullName, "included.md"),
                "needle",
                cancellationToken
            );
            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory.FullName, "ignored.cs"),
                "needle",
                cancellationToken
            );
            var store = CreateStore(workspace);

            var results = await store.SearchAsync(
                string.Empty,
                "needle",
                "src/**/*.md",
                recursive: true,
                cancellationToken: cancellationToken
            );

            var result = Assert.Single(results);
            Assert.Equal("src/included.md", result.FileName);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static ProjectAgentFileStore CreateStore(string workspace)
    {
        return new ProjectAgentFileStore(
            new StubFileSystemResolver(new LocalFileSystem(workspace)),
            Guid.CreateVersion7()
        );
    }

    private sealed class StubFileSystemResolver : IAgwFileSystemResolver
    {
        private readonly IAgwFileSystem _fileSystem;

        public StubFileSystemResolver(IAgwFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }

        public Task<IAgwFileSystem> ResolveAsync(Guid projectId, CancellationToken ct) => Task.FromResult(_fileSystem);
    }
}
