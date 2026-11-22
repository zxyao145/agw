using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Agw.Testing;
using Agw.Tools.Impl.ToolBlocks.FileAccess;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Tests;

public sealed class AgwFileAccessProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        "file-access-provider",
        Guid.CreateVersion7().ToString("N")
    );
    private readonly Guid _directoryId = Guid.CreateVersion7();
    private readonly string _primary;
    private readonly string _extra;
    private readonly AgwFileAccessProvider _provider;

    public AgwFileAccessProviderTests()
    {
        _primary = Directory.CreateDirectory(Path.Combine(_root, "primary")).FullName;
        _extra = Directory.CreateDirectory(Path.Combine(_root, "extra")).FullName;
        var projectId = Guid.CreateVersion7();
        var snapshot = ProjectWorkspacePaths.CreateSnapshot(
            projectId,
            _primary,
            [new ProjectWorkspaceDirectory(_directoryId, _extra)]
        );
        _provider = new AgwFileAccessProvider(new SnapshotFileSystemResolver(), projectId, snapshot);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task WriteAsync_ExistingFileWithoutOverwrite_KeepsContent()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_primary, "notes.md");
        await File.WriteAllTextAsync(path, "original", token);

        var result = await InvokeAsync(
            AgwFileAccessProvider.WriteToolName,
            ("fileName", "notes.md"),
            ("content", "changed")
        );

        Assert.Equal(
            "File 'notes.md' already exists. To replace it, write again with overwrite set to true.",
            ResultText(result)
        );
        Assert.Equal("original", await File.ReadAllTextAsync(path, token));
    }

    [Fact]
    public async Task GrepAsync_Directory_ReturnsRootRelativePathAndLineReadableByReadLines()
    {
        var token = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_primary, "docs"));
        await File.WriteAllTextAsync(Path.Combine(_primary, "docs", "guide.md"), "alpha\r\nneedle\rgamma\n", token);

        var searchResult = Assert.IsType<JsonElement>(
            await InvokeAsync(AgwFileAccessProvider.GrepToolName, ("regexPattern", "NEEDLE"), ("directory", "docs"))
        );
        var file = Assert.Single(searchResult.GetProperty("files").EnumerateArray().ToArray());
        var match = Assert.Single(file.GetProperty("matchingLines").EnumerateArray().ToArray());
        var fileName = file.GetProperty("fileName").GetString();
        var lineNumber = match.GetProperty("lineNumber").GetInt32();
        var lines = await InvokeAsync(
            AgwFileAccessProvider.ReadLinesToolName,
            ("fileName", fileName),
            ("startLine", lineNumber),
            ("endLine", lineNumber)
        );

        Assert.Equal("docs/guide.md", fileName);
        Assert.Equal(2, lineNumber);
        Assert.Equal("2\tneedle\r", ResultText(lines));
    }

    [Fact]
    public async Task ReadLinesAsync_EndLinePastLastLine_ReadsToEnd()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_primary, "notes.txt"), "a\nb\nc\n", token);

        var result = await InvokeAsync(
            AgwFileAccessProvider.ReadLinesToolName,
            ("fileName", "notes.txt"),
            ("startLine", 2),
            ("endLine", 10)
        );

        Assert.Equal("2\tb\n3\tc\n", ResultText(result));
    }

    [Fact]
    public async Task ReadAsync_LargeSingleLine_ReturnsBoundedPagesWithoutBreakingTextUnits()
    {
        const int pageContentCharacterLimit = 16 * 1024;
        var token = TestContext.Current.CancellationToken;
        var content = string.Concat(
            new string('界', pageContentCharacterLimit - 2),
            "😀",
            "\r\n",
            new string('界', pageContentCharacterLimit - 3),
            "\r\nend"
        );
        await File.WriteAllTextAsync(Path.Combine(_primary, "x"), content, token);

        var pages = new List<string>();
        JsonElement? continuation = null;
        while (true)
        {
            var result = continuation is null
                ? await InvokeAsync(AgwFileAccessProvider.ReadFileToolName, ("fileName", "x"))
                : await InvokeAsync(
                    AgwFileAccessProvider.ReadFileToolName,
                    ("fileName", "x"),
                    ("continuation", continuation.Value)
                );
            var page = ReadPage(result);
            pages.Add(page.Content);
            Assert.True(page.Content.Length <= pageContentCharacterLimit);
            Assert.False(char.IsHighSurrogate(page.Content[^1]));
            Assert.NotEqual('\r', page.Content[^1]);
            continuation = page.Continuation;
            if (continuation is null)
            {
                break;
            }
        }

        Assert.Equal(4, pages.Count);
        Assert.True(char.IsLowSurrogate(pages[0][^1]));
        Assert.Equal("\r\n", pages[1]);
        Assert.StartsWith(new string('界', pageContentCharacterLimit - 3), pages[2], StringComparison.Ordinal);
        Assert.Equal("end", pages[3]);
        Assert.Equal(content, string.Concat(pages));
    }

    [Theory]
    [InlineData(65001)]
    [InlineData(1200)]
    [InlineData(1201)]
    [InlineData(12000)]
    [InlineData(12001)]
    public async Task ReadAsync_UnicodeBom_PaginatesWideTextAfterSeek(int codePage)
    {
        var token = TestContext.Current.CancellationToken;
        var encoding = Encoding.GetEncoding(codePage);
        var content = string.Concat(new string('界', 16 * 1024 - 1), "😀尾");
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(_primary, "utf16.txt"), bytes, token);

        var first = ReadPage(await InvokeAsync(AgwFileAccessProvider.ReadFileToolName, ("fileName", "utf16.txt")));
        Assert.NotNull(first.Continuation);
        Assert.False(char.IsHighSurrogate(first.Content[^1]));
        var lastResult = await InvokeAsync(
            AgwFileAccessProvider.ReadFileToolName,
            ("fileName", "utf16.txt"),
            ("continuation", first.Continuation.Value)
        );
        var lastJson = Assert.IsType<JsonElement>(lastResult);
        var last = ReadPage(lastJson);

        Assert.Equal(JsonValueKind.Object, lastJson.ValueKind);
        Assert.False(
            lastJson.TryGetProperty("continuation", out var lastContinuation)
                && lastContinuation.ValueKind != JsonValueKind.Null
        );
        Assert.Null(last.Continuation);
        Assert.Equal(content, string.Concat(first.Content, last.Content));
    }

    [Theory]
    [InlineData(65001)]
    [InlineData(1200)]
    [InlineData(1201)]
    [InlineData(12000)]
    [InlineData(12001)]
    public async Task ReadAsync_EmbeddedBomAtContinuationBoundary_PreservesCharacter(int codePage)
    {
        var token = TestContext.Current.CancellationToken;
        var content = string.Concat(new string('a', 16 * 1024), "\uFEFFtail");
        var encoding = Encoding.GetEncoding(codePage);
        await File.WriteAllTextAsync(Path.Combine(_primary, "embedded-bom.txt"), content, encoding, token);

        var first = ReadPage(
            await InvokeAsync(AgwFileAccessProvider.ReadFileToolName, ("fileName", "embedded-bom.txt"))
        );
        Assert.NotNull(first.Continuation);
        var lastResult = await InvokeAsync(
            AgwFileAccessProvider.ReadFileToolName,
            ("fileName", "embedded-bom.txt"),
            ("continuation", first.Continuation.Value)
        );
        var lastJson = Assert.IsType<JsonElement>(lastResult);
        var last = ReadPage(lastJson);

        Assert.Equal(JsonValueKind.Object, lastJson.ValueKind);
        Assert.Equal("\uFEFFtail", last.Content);
        Assert.Equal(content, string.Concat(first.Content, last.Content));
    }

    [Fact]
    public async Task ReadAsync_NormalLinePastRemainingPageBudget_StartsOnNextPage()
    {
        var token = TestContext.Current.CancellationToken;
        var firstLine = new string('a', 16 * 1024 - 100);
        var nextLine = new string('界', 8 * 1024 + 8);
        await File.WriteAllTextAsync(
            Path.Combine(_primary, "normal-lines.txt"),
            $"{firstLine}\n{nextLine}\r\nend",
            token
        );

        var first = ReadPage(
            await InvokeAsync(AgwFileAccessProvider.ReadFileToolName, ("fileName", "normal-lines.txt"))
        );
        Assert.NotNull(first.Continuation);
        var lastResult = await InvokeAsync(
            AgwFileAccessProvider.ReadFileToolName,
            ("fileName", "normal-lines.txt"),
            ("continuation", first.Continuation.Value)
        );
        var lastJson = Assert.IsType<JsonElement>(lastResult);
        var last = ReadPage(lastJson);

        Assert.Equal($"{firstLine}\n", first.Content);
        Assert.Equal($"{nextLine}\r\nend", last.Content);
        Assert.False(
            lastJson.TryGetProperty("continuation", out var lastContinuation)
                && lastContinuation.ValueKind != JsonValueKind.Null
        );
    }

    [Fact]
    public async Task ReadAsync_InvalidUtf8_PaginatesUsingDecodedOffsets()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_primary, "invalid-utf8.txt");
        var bytes = Encoding
            .UTF8.GetBytes(new string('a', 1_000))
            .Concat(new byte[] { 0xFF })
            .Concat(Encoding.UTF8.GetBytes(new string('b', 16 * 1024)))
            .ToArray();
        await File.WriteAllBytesAsync(path, bytes, token);
        var expected = await File.ReadAllTextAsync(path, token);

        var pages = new List<string>();
        JsonElement? continuation = null;
        while (true)
        {
            var result = continuation is null
                ? await InvokeAsync(AgwFileAccessProvider.ReadFileToolName, ("fileName", "invalid-utf8.txt"))
                : await InvokeAsync(
                    AgwFileAccessProvider.ReadFileToolName,
                    ("fileName", "invalid-utf8.txt"),
                    ("continuation", continuation.Value)
                );
            var page = ReadPage(result);
            pages.Add(page.Content);
            continuation = page.Continuation;
            if (continuation is null)
            {
                break;
            }
        }

        Assert.Equal(expected, string.Concat(pages));
    }

    [Fact]
    public async Task ReadAsync_ChangedFile_RejectsStaleContinuation()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_primary, "stale.txt"), new string('x', 16 * 1024 + 1), token);
        var first = ReadPage(await InvokeAsync(AgwFileAccessProvider.ReadFileToolName, ("fileName", "stale.txt")));
        Assert.NotNull(first.Continuation);
        await File.WriteAllTextAsync(Path.Combine(_primary, "stale.txt"), "changed size", token);

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await InvokeAsync(
                AgwFileAccessProvider.ReadFileToolName,
                ("fileName", "stale.txt"),
                ("continuation", first.Continuation.Value)
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task ReadAsync_ReplacedFileWithSameSizeAndTimestamp_RejectsStaleContinuation()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_primary, "replaced.txt");
        var replacementPath = Path.Combine(_primary, "replacement.txt");
        await File.WriteAllTextAsync(path, new string('x', 16 * 1024 + 1), token);
        var previousWriteTime = File.GetLastWriteTimeUtc(path);
        var first = ReadPage(await InvokeAsync(AgwFileAccessProvider.ReadFileToolName, ("fileName", "replaced.txt")));
        Assert.NotNull(first.Continuation);

        await File.WriteAllTextAsync(replacementPath, new string('y', 16 * 1024 + 1), token);
        File.SetLastWriteTimeUtc(replacementPath, previousWriteTime);
        Assert.Equal(previousWriteTime, File.GetLastWriteTimeUtc(replacementPath));
        File.Move(replacementPath, path, overwrite: true);
        Assert.Equal(previousWriteTime, File.GetLastWriteTimeUtc(path));

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await InvokeAsync(
                AgwFileAccessProvider.ReadFileToolName,
                ("fileName", "replaced.txt"),
                ("continuation", first.Continuation.Value)
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task ReadAsync_DeletedFileWithContinuation_RejectsContinuation()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_primary, "removed.txt");
        await File.WriteAllTextAsync(path, new string('x', 16 * 1024 + 1), token);
        var first = ReadPage(await InvokeAsync(AgwFileAccessProvider.ReadFileToolName, ("fileName", "removed.txt")));
        Assert.NotNull(first.Continuation);
        File.Delete(path);

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await InvokeAsync(
                AgwFileAccessProvider.ReadFileToolName,
                ("fileName", "removed.txt"),
                ("continuation", first.Continuation.Value)
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Theory]
    [InlineData("lineNumber", "5")]
    [InlineData("byteOffset", "1")]
    [InlineData("characterOffset", "1")]
    [InlineData("lineHasContent", "true")]
    [InlineData("codePage", "1200")]
    public async Task ReadLinesAsync_ChangedContinuationPosition_ThrowsInvalidParam(string field, string value)
    {
        var token = TestContext.Current.CancellationToken;
        const string fileName = "numbered.txt";
        var content = string.Concat(Enumerable.Range(1, 205).Select(line => $"line-{line:D3}\n"));
        await File.WriteAllTextAsync(Path.Combine(_primary, fileName), content, token);

        var first = ReadPage(
            await InvokeAsync(AgwFileAccessProvider.ReadLinesToolName, ("fileName", fileName), ("startLine", 1))
        );
        Assert.NotNull(first.Continuation);
        var continuation = JsonNode.Parse(first.Continuation.Value.GetRawText())!.AsObject();
        continuation[field] = JsonNode.Parse(value);

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await InvokeAsync(
                AgwFileAccessProvider.ReadLinesToolName,
                ("fileName", fileName),
                ("startLine", 1),
                ("continuation", JsonSerializer.SerializeToElement(continuation))
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task ReadLinesAsync_LargeRange_ReturnsNumberedPagesWithinRequestedRange()
    {
        var token = TestContext.Current.CancellationToken;
        var content = string.Concat(Enumerable.Range(1, 205).Select(line => $"line-{line:D3}\r\n"));
        await File.WriteAllTextAsync(Path.Combine(_primary, "large.txt"), content, token);

        var pages = new List<string>();
        JsonElement? continuation = null;
        while (true)
        {
            var result = continuation is null
                ? await InvokeAsync(
                    AgwFileAccessProvider.ReadLinesToolName,
                    ("fileName", "large.txt"),
                    ("startLine", 3),
                    ("endLine", 205)
                )
                : await InvokeAsync(
                    AgwFileAccessProvider.ReadLinesToolName,
                    ("fileName", "large.txt"),
                    ("startLine", 3),
                    ("endLine", 205),
                    ("continuation", continuation.Value)
                );
            var page = ReadPage(result);
            pages.Add(page.Content);
            continuation = page.Continuation;
            if (continuation is null)
            {
                break;
            }
        }

        Assert.Equal(2, pages.Count);
        Assert.Equal(
            string.Concat(Enumerable.Range(3, 203).Select(line => $"{line}\tline-{line:D3}\r\n")),
            string.Concat(pages)
        );
    }

    [Fact]
    public async Task ReadLinesAsync_LargeSingleLine_RepeatsNumberPrefixAcrossPages()
    {
        var token = TestContext.Current.CancellationToken;
        var line = string.Concat(new string('界', 16 * 1024 + 20), "😀");
        await File.WriteAllTextAsync(Path.Combine(_primary, "long-line.txt"), $"{line}\r\nnext\n", token);

        var firstResult = await InvokeAsync(
            AgwFileAccessProvider.ReadLinesToolName,
            ("fileName", "long-line.txt"),
            ("startLine", 1),
            ("endLine", 2)
        );
        var firstJson = Assert.IsType<JsonElement>(firstResult);
        var first = ReadPage(firstJson);
        Assert.NotNull(first.Continuation);

        var lastResult = await InvokeAsync(
            AgwFileAccessProvider.ReadLinesToolName,
            ("fileName", "long-line.txt"),
            ("startLine", 1),
            ("endLine", 2),
            ("continuation", first.Continuation.Value)
        );
        var lastJson = Assert.IsType<JsonElement>(lastResult);
        var last = ReadPage(lastJson);
        var terminatorIndex = last.Content.IndexOf("\r\n", 2, StringComparison.Ordinal);

        Assert.Equal(JsonValueKind.Object, firstJson.ValueKind);
        Assert.Equal(JsonValueKind.Object, lastJson.ValueKind);
        Assert.False(
            lastJson.TryGetProperty("continuation", out var lastContinuation)
                && lastContinuation.ValueKind != JsonValueKind.Null
        );
        Assert.StartsWith("1\t", first.Content, StringComparison.Ordinal);
        Assert.StartsWith("1\t", last.Content, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(first.Content[^1]));
        Assert.NotEqual('\r', first.Content[^1]);
        Assert.Equal(line, first.Content[2..] + last.Content[2..terminatorIndex]);
        Assert.Equal("2\tnext\n", last.Content[(terminatorIndex + 2)..]);
    }

    [Fact]
    public async Task ReadLinesAsync_MissingFile_ReturnsNotFound()
    {
        var result = await InvokeAsync(
            AgwFileAccessProvider.ReadLinesToolName,
            ("fileName", "missing.txt"),
            ("startLine", 1)
        );

        Assert.Equal("File 'missing.txt' not found.", ResultText(result));
    }

    [Fact]
    public async Task ReplaceLinesAsync_MatchingExpectedLine_ReplacesThatLine()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_primary, "notes.txt");
        await File.WriteAllTextAsync(path, "alpha\r\nbeta\r\ngamma\r\n", token);

        var result = await InvokeAsync(
            AgwFileAccessProvider.ReplaceLinesToolName,
            ("fileName", "notes.txt"),
            (
                "edits",
                new List<AgwFileLineEdit>
                {
                    new()
                    {
                        LineNumber = 2,
                        NewLine = "delta\r\n",
                        ExpectedLine = "beta",
                    },
                }
            )
        );

        Assert.Equal("Replaced 1 line(s) in 'notes.txt'.", ResultText(result));
        Assert.Equal("alpha\r\ndelta\r\ngamma\r\n", await File.ReadAllTextAsync(path, token));
    }

    [Fact]
    public async Task ReplaceLinesAsync_ExpectedLineMismatch_ThrowsInvalidParamAndKeepsContent()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_primary, "notes.txt");
        await File.WriteAllTextAsync(path, "alpha\nbeta\n", token);

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await InvokeAsync(
                AgwFileAccessProvider.ReplaceLinesToolName,
                ("fileName", "notes.txt"),
                (
                    "edits",
                    new List<AgwFileLineEdit>
                    {
                        new()
                        {
                            LineNumber = 2,
                            NewLine = "delta\n",
                            ExpectedLine = "stale",
                        },
                    }
                )
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains("Re-read the file", exception.Message, StringComparison.Ordinal);
        Assert.Equal("alpha\nbeta\n", await File.ReadAllTextAsync(path, token));
    }

    [Fact]
    public async Task ReplaceAsync_MultipleOccurrencesWithoutReplaceAll_ThrowsMultipleMatches()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_primary, "notes.txt");
        await File.WriteAllTextAsync(path, "value value", token);

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await InvokeAsync(
                AgwFileAccessProvider.ReplaceToolName,
                ("fileName", "notes.txt"),
                ("oldString", "value"),
                ("newString", "other")
            )
        );

        Assert.Equal(ErrorCodes.MultipleMatches.Code, exception.Code);
        Assert.Contains("replaceAll=true", exception.Message, StringComparison.Ordinal);
        Assert.Equal("value value", await File.ReadAllTextAsync(path, token));
    }

    [Fact]
    public async Task ReplaceAsync_ReplaceAll_ReplacesEveryOccurrence()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_primary, "notes.txt");
        await File.WriteAllTextAsync(path, "value value", token);

        var result = await InvokeAsync(
            AgwFileAccessProvider.ReplaceToolName,
            ("fileName", "notes.txt"),
            ("oldString", "value"),
            ("newString", "other"),
            ("replaceAll", true)
        );

        Assert.Equal("Replaced 2 occurrence(s) in 'notes.txt'.", ResultText(result));
        Assert.Equal("other other", await File.ReadAllTextAsync(path, token));
    }

    [Fact]
    public async Task LsAsync_GlobPattern_FiltersEntriesByName()
    {
        var token = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_primary, "reports"));
        await File.WriteAllTextAsync(Path.Combine(_primary, "a.md"), "a", token);
        await File.WriteAllTextAsync(Path.Combine(_primary, "b.txt"), "b", token);

        var all = ResultList<AgwFileStoreEntry>(await InvokeAsync(AgwFileAccessProvider.LsToolName));
        var markdown = ResultList<AgwFileStoreEntry>(
            await InvokeAsync(AgwFileAccessProvider.LsToolName, ("globPattern", "*.md"))
        );

        Assert.Equal(
            [
                ("reports", AgwFileStoreEntry.Directory),
                ("a.md", AgwFileStoreEntry.File),
                ("b.txt", AgwFileStoreEntry.File),
            ],
            all.Select(static entry => (entry.Name, entry.Type))
        );
        Assert.Equal("a.md", Assert.Single(markdown).Name);
    }

    [Fact]
    public async Task DeleteAsync_AdditionalDirectory_DeletesOnlyThatDirectoryFile()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_primary, "notes.txt"), "primary", token);
        await File.WriteAllTextAsync(Path.Combine(_extra, "notes.txt"), "extra", token);

        var deleted = await InvokeAsync(
            AgwFileAccessProvider.DeleteFileToolName,
            ("fileName", "notes.txt"),
            ("directoryId", _directoryId.ToString())
        );
        var missing = await InvokeAsync(
            AgwFileAccessProvider.DeleteFileToolName,
            ("fileName", "notes.txt"),
            ("directoryId", _directoryId.ToString())
        );

        Assert.Equal("File 'notes.txt' deleted.", ResultText(deleted));
        Assert.Equal("File 'notes.txt' not found.", ResultText(missing));
        Assert.False(File.Exists(Path.Combine(_extra, "notes.txt")));
        Assert.Equal("primary", await File.ReadAllTextAsync(Path.Combine(_primary, "notes.txt"), token));
    }

    [Fact]
    public async Task ReadAsync_PathTraversal_ThrowsPathOutsideRoot()
    {
        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await InvokeAsync(AgwFileAccessProvider.ReadFileToolName, ("fileName", "../extra/secret.txt"))
        );

        Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task ReadAsync_InvalidDirectoryId_ThrowsInvalidParam(string directoryId)
    {
        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await InvokeAsync(
                AgwFileAccessProvider.ReadFileToolName,
                ("fileName", "notes.txt"),
                ("directoryId", directoryId)
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task ReadAsync_UnknownDirectoryId_ThrowsResourceNotFound()
    {
        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await InvokeAsync(
                AgwFileAccessProvider.ReadFileToolName,
                ("fileName", "notes.txt"),
                ("directoryId", Guid.CreateVersion7().ToString())
            )
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
    }

    private async Task<object?> InvokeAsync(string toolName, params (string Name, object? Value)[] arguments)
    {
        var token = TestContext.Current.CancellationToken;
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(_provider.Tools, tool => tool.Name == toolName));
        return await tool.InvokeAsync(
            new AIFunctionArguments(
                arguments.ToDictionary(static argument => argument.Name, static argument => argument.Value)
            ),
            token
        );
    }

    private static string? ResultText(object? result) => Assert.IsType<JsonElement>(result).GetString();

    private static (string Content, JsonElement? Continuation) ReadPage(object? result)
    {
        var json = Assert.IsType<JsonElement>(result);
        if (json.ValueKind == JsonValueKind.String)
        {
            return (json.GetString()!, null);
        }

        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        json.TryGetProperty("continuation", out var continuation);
        return (
            json.GetProperty("content").GetString()!,
            continuation.ValueKind == JsonValueKind.Undefined || continuation.ValueKind == JsonValueKind.Null
                ? null
                : continuation.Clone()
        );
    }

    private static List<T> ResultList<T>(object? result) =>
        Assert.IsType<JsonElement>(result).Deserialize<List<T>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private sealed class SnapshotFileSystemResolver : IAgwFileSystemResolver
    {
        public Task<IAgwFileSystem?> ResolveAsync(Guid projectId, CancellationToken ct) =>
            throw new InvalidOperationException("The file access provider must resolve from its captured snapshot.");

        public Task<IAgwFileSystem?> ResolveSnapshotAsync(
            Guid projectId,
            ProjectWorkspaceSnapshot snapshot,
            Guid? directoryId,
            CancellationToken ct
        )
        {
            var path =
                directoryId == null
                    ? snapshot.Workspace
                    : snapshot.AdditionalDirectories.Single(directory => directory.Id == directoryId).Path;
            return Task.FromResult<IAgwFileSystem?>(TestFileSystems.Local(path));
        }
    }
}
