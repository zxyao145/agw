using System.Text;
using Agw.Files.Abstracts.Dtos;
using Agw.Files.Infrastructure.Storage;

namespace Agw.Files.Tests;

public sealed class LocalFileSystemSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        "local-filesystem-search",
        Guid.CreateVersion7().ToString("N")
    );

    public LocalFileSystemSearchTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task SearchAsync_MixedLineBreaks_ReportsReadLineNumbers()
    {
        await WriteAsync("notes.txt", "alpha\r\nbeta\rgamma\nbeta-end");

        var hits = await SearchAsync(new SearchOptions("beta"));

        Assert.Equal([("notes.txt", 2, "beta"), ("notes.txt", 4, "beta-end")], hits);
    }

    [Fact]
    public async Task SearchAsync_LineWithNullCharacter_StopsTheFile()
    {
        await WriteAsync("mixed.txt", "match one\nbin\0ary\nmatch two");

        var hits = await SearchAsync(new SearchOptions("match"));

        Assert.Equal([("mixed.txt", 1, "match one")], hits);
    }

    [Fact]
    public async Task SearchAsync_ExcludedDirectoryName_IgnoresCase()
    {
        await WriteAsync("node_modules/lib.txt", "needle");
        await WriteAsync("src/app.txt", "needle");

        var hits = await SearchAsync(new SearchOptions("needle", ExcludedDirectoryNames: ["Node_Modules"]));

        Assert.Equal([("src/app.txt", 1, "needle")], hits);
    }

    [Fact]
    public async Task SearchAsync_IncludeExtensions_MatchesLowercasedExtension()
    {
        await WriteAsync("Program.CS", "needle");
        await WriteAsync("readme.txt", "needle");

        var hits = await SearchAsync(new SearchOptions("needle", IncludeExtensions: ["cs"]));

        Assert.Equal([("Program.CS", 1, "needle")], hits);
    }

    [Fact]
    public async Task SearchAsync_MaxHits_StopsAcrossFiles()
    {
        await WriteAsync("a.txt", "needle 1\nneedle 2");
        await WriteAsync("b.txt", "needle 3");

        var hits = await SearchAsync(new SearchOptions("needle", MaxHits: 2));

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task SearchAsync_MultibyteText_MatchesDecodedLines()
    {
        await WriteAsync("zh.txt", "第一行\n包含关键字的第二行\n😀 表情");

        var hits = await SearchAsync(new SearchOptions("关键字|😀"));

        Assert.Equal([("zh.txt", 2, "包含关键字的第二行"), ("zh.txt", 3, "😀 表情")], hits);
    }

    [Fact]
    public async Task SearchAsync_FileBeyondBufferLimit_ReadsLineByLine()
    {
        var path = Path.Combine(_root, "large.txt");
        await using (var writer = new StreamWriter(path, append: false, new UTF8Encoding(false)))
        {
            var line = new string('x', 1023);
            for (var index = 0; index < 17 * 1024; index++)
            {
                await writer.WriteLineAsync(line);
            }
            await writer.WriteAsync("needle at the end");
        }

        var hits = await SearchAsync(new SearchOptions("needle"));

        Assert.Equal([("large.txt", 17 * 1024 + 1, "needle at the end")], hits);
    }

    [Fact]
    public async Task EnumerateAsync_Entries_ReportTypeSizeAndRelativePath()
    {
        await WriteAsync("docs/guide.md", "12345");
        var fileSystem = new LocalFileSystem(_root);

        var entries = new List<FileEntry>();
        await foreach (
            var entry in fileSystem.EnumerateAsync("", "*", recursive: true, TestContext.Current.CancellationToken)
        )
        {
            entries.Add(entry);
        }

        var directory = Assert.Single(entries, entry => entry.Path == "docs");
        Assert.True(directory.IsDirectory);
        Assert.Equal(0, directory.Size);
        var file = Assert.Single(entries, entry => entry.Path == "docs/guide.md");
        Assert.False(file.IsDirectory);
        Assert.Equal(5, file.Size);
        Assert.Equal(
            File.GetLastWriteTimeUtc(Path.Combine(_root, "docs", "guide.md")),
            file.LastModifiedUtc.UtcDateTime
        );
    }

    private async Task WriteAsync(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), TestContext.Current.CancellationToken);
    }

    private async Task<List<(string Path, int LineNumber, string Line)>> SearchAsync(SearchOptions options)
    {
        var fileSystem = new LocalFileSystem(_root);
        var hits = new List<(string, int, string)>();
        await foreach (var hit in fileSystem.SearchAsync("", options, TestContext.Current.CancellationToken))
        {
            hits.Add((hit.Path, hit.LineNumber, hit.Line));
        }

        return hits.OrderBy(hit => hit.Item1, StringComparer.Ordinal).ThenBy(hit => hit.Item2).ToList();
    }
}
