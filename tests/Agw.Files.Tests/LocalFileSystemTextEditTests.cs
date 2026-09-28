using Agw.Files.Abstracts.Dtos;
using Agw.Files.Infrastructure.Storage;
using Agw.Shared.Exceptions;

namespace Agw.Files.Tests;

public sealed class LocalFileSystemTextEditTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        "local-filesystem-text-edit",
        Guid.CreateVersion7().ToString("N")
    );

    private readonly LocalFileSystem _fileSystem;

    public LocalFileSystemTextEditTests()
    {
        Directory.CreateDirectory(_root);
        _fileSystem = new LocalFileSystem(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task CreateTextFileAsync_ExistingFile_ReturnsFalseAndKeepsContent()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.md"), "original", token);

        var created = await _fileSystem.CreateTextFileAsync("notes.md", "changed", token);

        Assert.False(created);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(_root, "notes.md"), token));
    }

    [Fact]
    public async Task CreateTextFileAsync_MissingParentDirectory_CreatesFile()
    {
        var token = TestContext.Current.CancellationToken;

        var created = await _fileSystem.CreateTextFileAsync("docs/guide.md", "content", token);

        Assert.True(created);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(_root, "docs", "guide.md"), token));
    }

    [Fact]
    public async Task CreateTextFileAsync_EmptyPath_ThrowsFilePathRequired()
    {
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _fileSystem.CreateTextFileAsync(string.Empty, "content", TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.FilePathRequired.Code, exception.Code);
    }

    [Fact]
    public async Task ReadLinesAsync_MixedTerminators_KeepsEachTerminator()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "a\r\nb\rc\n", token);

        var lines = await _fileSystem.ReadLinesAsync("notes.txt", 1, null, token);

        Assert.Equal(["a\r\n", "b\r", "c\n"], lines);
    }

    [Fact]
    public async Task ReadLinesAsync_EndLinePastLastLine_ClampsToLastLine()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "a\nb\nc", token);

        var lines = await _fileSystem.ReadLinesAsync("notes.txt", 2, 10, token);

        Assert.Equal(["b\n", "c"], lines);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(4, null)]
    [InlineData(2, 1)]
    public async Task ReadLinesAsync_InvalidRange_ThrowsInvalidParam(int startLine, int? endLine)
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "a\nb\nc\n", token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _fileSystem.ReadLinesAsync("notes.txt", startLine, endLine, token)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task ReadLinesAsync_MissingFile_ThrowsFileNotFound()
    {
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _fileSystem.ReadLinesAsync("missing.txt", 1, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.FileNotFound.Code, exception.Code);
    }

    [Fact]
    public async Task ReplaceTextAsync_ReplaceAll_ReturnsCountAndWritesContent()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "value value", token);

        var count = await _fileSystem.ReplaceTextAsync("notes.txt", "value", "other", replaceAll: true, token);

        Assert.Equal(2, count);
        Assert.Equal("other other", await File.ReadAllTextAsync(Path.Combine(_root, "notes.txt"), token));
    }

    [Fact]
    public async Task ReplaceTextAsync_MultipleOccurrencesWithoutReplaceAll_ThrowsMultipleMatchesAndKeepsContent()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "value value", token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _fileSystem.ReplaceTextAsync("notes.txt", "value", "other", replaceAll: false, token)
        );

        Assert.Equal(ErrorCodes.MultipleMatches.Code, exception.Code);
        Assert.Contains("replaceAll=true", exception.Message, StringComparison.Ordinal);
        Assert.Equal("value value", await File.ReadAllTextAsync(Path.Combine(_root, "notes.txt"), token));
    }

    [Fact]
    public async Task ReplaceTextAsync_ConcurrentEditsFromSeparateInstances_KeepsEveryEdit()
    {
        var token = TestContext.Current.CancellationToken;
        var tokens = Enumerable.Range(0, 20).Select(static index => $"t{index:D2}").ToArray();
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), string.Join(' ', tokens), token);
        LocalFileSystem[] fileSystems = [_fileSystem, new LocalFileSystem(_root)];

        await Task.WhenAll(
            tokens.Select(
                (value, index) =>
                    Task.Run(
                        () =>
                            fileSystems[index % 2]
                                .ReplaceTextAsync("notes.txt", value, $"d{value[1..]}", replaceAll: false, token),
                        token
                    )
            )
        );

        Assert.Equal(
            string.Join(' ', tokens.Select(static value => $"d{value[1..]}")),
            await File.ReadAllTextAsync(Path.Combine(_root, "notes.txt"), token)
        );
    }

    [Fact]
    public async Task ReplaceLinesAsync_EmptyNewLine_DeletesLineAndItsBreak()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "alpha\nbeta\ngamma", token);

        await _fileSystem.ReplaceLinesAsync("notes.txt", [Edit(2, string.Empty)], token);

        Assert.Equal("alpha\ngamma", await File.ReadAllTextAsync(Path.Combine(_root, "notes.txt"), token));
    }

    [Fact]
    public async Task ReplaceLinesAsync_MatchingExpectedLine_ReplacesThatLine()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "alpha\r\nbeta\r\ngamma\r\n", token);

        await _fileSystem.ReplaceLinesAsync("notes.txt", [Edit(2, "delta\r\n", "beta")], token);

        Assert.Equal(
            "alpha\r\ndelta\r\ngamma\r\n",
            await File.ReadAllTextAsync(Path.Combine(_root, "notes.txt"), token)
        );
    }

    [Fact]
    public async Task ReplaceLinesAsync_ExpectedLineMismatch_ThrowsInvalidParamAndKeepsContent()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "alpha\nbeta\n", token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _fileSystem.ReplaceLinesAsync("notes.txt", [Edit(2, "delta\n", "stale")], token)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains("Re-read the file", exception.Message, StringComparison.Ordinal);
        Assert.Equal("alpha\nbeta\n", await File.ReadAllTextAsync(Path.Combine(_root, "notes.txt"), token));
    }

    [Fact]
    public async Task ReplaceLinesAsync_DuplicateLineNumber_ThrowsInvalidParam()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "alpha\nbeta\n", token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _fileSystem.ReplaceLinesAsync("notes.txt", [Edit(1, "one\n"), Edit(1, "two\n")], token)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task ReplaceLinesAsync_LineNumberOutOfRange_ThrowsInvalidParam(int lineNumber)
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "alpha\nbeta\n", token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _fileSystem.ReplaceLinesAsync("notes.txt", [Edit(lineNumber, "x\n")], token)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task GetSubFileSystem_RelativePath_WritesBelowSubdirectory()
    {
        var token = TestContext.Current.CancellationToken;
        var memory = _fileSystem.GetSubFileSystem(".agw/memory");

        await memory.WriteAllTextAsync("notes.md", "memory", token);

        Assert.Equal("memory", await File.ReadAllTextAsync(Path.Combine(_root, ".agw", "memory", "notes.md"), token));
    }

    [Fact]
    public async Task GetSubFileSystem_PathEscapingSubdirectory_ThrowsPathOutsideRoot()
    {
        var memory = _fileSystem.GetSubFileSystem(".agw/memory");

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            memory.WriteAllTextAsync("../outside.md", "invalid", TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
        Assert.False(File.Exists(Path.Combine(_root, ".agw", "outside.md")));
    }

    [Fact]
    public async Task GetSubFileSystem_SearchAsync_ReturnsPathsRelativeToSubdirectory()
    {
        var token = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_root, "docs", "guides"));
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "guides", "setup.md"), "needle", token);
        var docs = _fileSystem.GetSubFileSystem("docs");

        var result = Assert.Single(
            await docs.SearchAsync(string.Empty, new SearchOptions("needle"), token).ToListAsync(token)
        );

        Assert.Equal("guides/setup.md", result.FileName);
        Assert.Equal((1, "needle"), (Assert.Single(result.MatchingLines).LineNumber, result.Snippet));
    }

    private static AgwFileLineEdit Edit(int lineNumber, string newLine, string? expectedLine = null) =>
        new()
        {
            LineNumber = lineNumber,
            NewLine = newLine,
            ExpectedLine = expectedLine,
        };
}
