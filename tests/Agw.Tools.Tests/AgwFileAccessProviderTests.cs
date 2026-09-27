using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Files.Abstracts;
using Agw.Files.Infrastructure.Storage;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Agw.Tools.Impl.ToolBlocks.FileAccess;
using Agw.Tools.Impl.ToolBlocks.Storage;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Tests;

public sealed class AgwFileAccessProviderTests : IAsyncDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("agw-file-access-provider-");
    private readonly Guid _directoryId = Guid.CreateVersion7();
    private readonly string _primary;
    private readonly string _extra;
    private readonly AgwFileAccessProvider _provider;

    public AgwFileAccessProviderTests()
    {
        _primary = _root.CreateSubdirectory("primary").FullName;
        _extra = _root.CreateSubdirectory("extra").FullName;
        var projectId = Guid.CreateVersion7();
        var snapshot = ProjectWorkspacePaths.CreateSnapshot(
            projectId,
            _primary,
            [new ProjectWorkspaceDirectory(_directoryId, _extra)]
        );
        _provider = new AgwFileAccessProvider(new SnapshotFileSystemResolver(), projectId, snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        _root.Delete(recursive: true);
    }

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

        var searchResult = Assert.Single(
            ResultList<AgwFileSearchResult>(
                await InvokeAsync(AgwFileAccessProvider.GrepToolName, ("regexPattern", "NEEDLE"), ("directory", "docs"))
            )
        );
        var match = Assert.Single(searchResult.MatchingLines);
        var lines = await InvokeAsync(
            AgwFileAccessProvider.ReadLinesToolName,
            ("fileName", searchResult.FileName),
            ("startLine", match.LineNumber),
            ("endLine", match.LineNumber)
        );

        Assert.Equal("docs/guide.md", searchResult.FileName);
        Assert.Equal(2, match.LineNumber);
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
    public async Task ReplaceAsync_MultipleOccurrencesWithoutReplaceAll_ThrowsInvalidParam()
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

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
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
    public async Task ReadAsync_PathTraversal_ThrowsInvalidParam()
    {
        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await InvokeAsync(AgwFileAccessProvider.ReadFileToolName, ("fileName", "../primary/secret.txt"))
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
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
        var agent = new ChatClientAgent(new StubChatClient());
        var context = await _provider.InvokingAsync(
            new AIContextProvider.InvokingContext(agent, null, new AIContext()),
            token
        );
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(context.Tools!, tool => tool.Name == toolName));
        return await tool.InvokeAsync(
            new AIFunctionArguments(
                arguments.ToDictionary(static argument => argument.Name, static argument => argument.Value)
            ),
            token
        );
    }

    private static string? ResultText(object? result) => Assert.IsType<JsonElement>(result).GetString();

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
            return Task.FromResult<IAgwFileSystem?>(new LocalFileSystem(path));
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "done")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }
    }
}
