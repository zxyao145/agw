using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Files.Infrastructure.Storage;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;
using Agw.Tools.Impl.ToolBlocks.ProjectMemory;
using Agw.Tools.Impl.ToolBlocks.Storage;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Tests;

public sealed class ProjectMemoryProviderTests : IDisposable
{
    private static readonly string[] ToolNames =
    [
        ProjectMemoryProvider.DeleteFileToolName,
        ProjectMemoryProvider.GrepToolName,
        ProjectMemoryProvider.LsToolName,
        ProjectMemoryProvider.ReadFileToolName,
        ProjectMemoryProvider.ReplaceToolName,
        ProjectMemoryProvider.ReplaceLinesToolName,
        ProjectMemoryProvider.WriteToolName,
    ];

    private readonly string _workspace = Directory
        .CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "project-memory-provider", Guid.CreateVersion7().ToString("N"))
        )
        .FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    [Fact]
    public async Task InvokingAsync_ExposesProjectToolsWithoutSessionState()
    {
        var provider = CreateProvider(CreateStore());

        var context = await InvokeProviderAsync(provider);
        var tools = context.Tools;
        Assert.NotNull(tools);

        Assert.Empty(provider.StateKeys);
        Assert.Contains("shared by all agents and conversations", context.Instructions);
        Assert.Equal(ToolNames, tools.Select(static tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.Null(context.Messages);
    }

    [Fact]
    public async Task InvokingAsync_MissingMemory_DoesNotCreateDirectory()
    {
        var store = new RecordingEmptyAgentFileStore();

        var context = await InvokeProviderAsync(CreateProvider(store));

        Assert.NotNull(context.Tools);
        Assert.Equal(0, store.CreateDirectoryCallCount);
    }

    [Fact]
    public async Task WriteAsync_NewProviderInstanceReadsSharedMemoryAndIndex()
    {
        var store = CreateStore();
        var firstProvider = CreateProvider(store);
        var firstContext = await InvokeProviderAsync(firstProvider);
        var write = GetFunction(firstContext, ProjectMemoryProvider.WriteToolName);

        var writeResult = await write.InvokeAsync(
            Arguments(
                ("fileName", "architecture.md"),
                ("content", "Use a modular monolith."),
                ("description", "Architecture decisions")
            ),
            TestContext.Current.CancellationToken
        );

        Assert.Equal("File 'architecture.md' written with description.", ResultText(writeResult));
        Assert.Equal(
            "Use a modular monolith.",
            await store.ReadAsync("architecture.md", TestContext.Current.CancellationToken)
        );
        var secondProvider = CreateProvider(store);
        var secondContext = await InvokeProviderAsync(secondProvider);
        var indexMessage = Assert.Single(secondContext.Messages!);
        Assert.Contains("architecture.md", indexMessage.Text);
        Assert.Contains("Architecture decisions", indexMessage.Text);

        var read = GetFunction(secondContext, ProjectMemoryProvider.ReadFileToolName);
        var readResult = await read.InvokeAsync(
            Arguments(("fileName", "architecture.md")),
            TestContext.Current.CancellationToken
        );
        Assert.Equal("Use a modular monolith.", ResultText(readResult));
    }

    [Fact]
    public async Task Tools_ListSearchReplaceLinesAndDeleteFollowMafBehavior()
    {
        var store = CreateStore();
        var context = await InvokeProviderAsync(CreateProvider(store));
        await GetFunction(context, ProjectMemoryProvider.WriteToolName)
            .InvokeAsync(
                Arguments(
                    ("fileName", "notes.md"),
                    ("content", "alpha\nbeta\ngamma\n"),
                    ("description", "Working notes")
                ),
                TestContext.Current.CancellationToken
            );

        var listResult = ResultList<FileListEntry>(
            await GetFunction(context, ProjectMemoryProvider.LsToolName)
                .InvokeAsync(Arguments(("globPattern", "*.md")), TestContext.Current.CancellationToken)
        );
        var listedFile = Assert.Single(listResult);
        Assert.Equal("notes.md", listedFile.Name);
        Assert.Equal("Working notes", listedFile.Description);

        var searchResult = ResultList<AgwFileSearchResult>(
            await GetFunction(context, ProjectMemoryProvider.GrepToolName)
                .InvokeAsync(Arguments(("regexPattern", "BETA")), TestContext.Current.CancellationToken)
        );
        Assert.Equal("notes.md", Assert.Single(searchResult).FileName);

        var replaceResult = await GetFunction(context, ProjectMemoryProvider.ReplaceToolName)
            .InvokeAsync(
                Arguments(("fileName", "notes.md"), ("oldString", "beta"), ("newString", "delta")),
                TestContext.Current.CancellationToken
            );
        Assert.Equal("Replaced 1 occurrence(s) in 'notes.md'.", ResultText(replaceResult));

        var replaceLinesResult = await GetFunction(context, ProjectMemoryProvider.ReplaceLinesToolName)
            .InvokeAsync(
                Arguments(
                    ("fileName", "notes.md"),
                    (
                        "edits",
                        new List<AgwFileLineEdit>
                        {
                            new() { LineNumber = 1, NewLine = "first\n" },
                            new() { LineNumber = 3, NewLine = string.Empty },
                        }
                    )
                ),
                TestContext.Current.CancellationToken
            );
        Assert.Equal("Replaced 2 line(s) in 'notes.md'.", ResultText(replaceLinesResult));
        Assert.Equal("first\ndelta\n", await store.ReadAsync("notes.md", TestContext.Current.CancellationToken));

        var deleteResult = await GetFunction(context, ProjectMemoryProvider.DeleteFileToolName)
            .InvokeAsync(Arguments(("fileName", "notes.md")), TestContext.Current.CancellationToken);
        Assert.Equal("File 'notes.md' deleted.", ResultText(deleteResult));
        Assert.Null(await store.ReadAsync("notes.md", TestContext.Current.CancellationToken));
        Assert.Null(await store.ReadAsync("notes_description.md", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentWrites_SeparateProvidersKeepCompleteSharedIndex()
    {
        var store = CreateStore();
        var firstContext = await InvokeProviderAsync(CreateProvider(store));
        var secondContext = await InvokeProviderAsync(CreateProvider(store));
        var cancellationToken = TestContext.Current.CancellationToken;

        await Task.WhenAll(
            GetFunction(firstContext, ProjectMemoryProvider.WriteToolName)
                .InvokeAsync(Arguments(("fileName", "first.md"), ("content", "first")), cancellationToken)
                .AsTask(),
            GetFunction(secondContext, ProjectMemoryProvider.WriteToolName)
                .InvokeAsync(Arguments(("fileName", "second.md"), ("content", "second")), cancellationToken)
                .AsTask()
        );

        var index = await store.ReadAsync("memories.md", cancellationToken);
        Assert.Contains("first.md", index);
        Assert.Contains("second.md", index);
    }

    [Fact]
    public async Task ReplaceLinesAsync_ExpectedLineMismatch_ThrowsInvalidParamAndKeepsContent()
    {
        var store = CreateStore();
        var context = await InvokeProviderAsync(CreateProvider(store));
        var cancellationToken = TestContext.Current.CancellationToken;
        await GetFunction(context, ProjectMemoryProvider.WriteToolName)
            .InvokeAsync(Arguments(("fileName", "notes.md"), ("content", "alpha\nbeta\n")), cancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await GetFunction(context, ProjectMemoryProvider.ReplaceLinesToolName)
                .InvokeAsync(
                    Arguments(
                        ("fileName", "notes.md"),
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
                    ),
                    cancellationToken
                )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Equal("alpha\nbeta\n", await store.ReadAsync("notes.md", cancellationToken));
    }

    [Fact]
    public async Task WriteAsync_NestedOrInternalNameIsRejected()
    {
        var context = await InvokeProviderAsync(CreateProvider(CreateStore()));
        var write = GetFunction(context, ProjectMemoryProvider.WriteToolName);

        var nested = await Assert.ThrowsAsync<AgwException>(async () =>
            await write.InvokeAsync(
                Arguments(("fileName", "folder/notes.md"), ("content", "invalid")),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Contains("flat names", nested.ToString(), StringComparison.OrdinalIgnoreCase);

        var internalFile = await Assert.ThrowsAsync<AgwException>(async () =>
            await write.InvokeAsync(
                Arguments(("fileName", "memories.md"), ("content", "invalid")),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Contains("reserved", internalFile.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_MemoryDirectoryReplacedBySymlinkOutsideWorkspace_IsRejected()
    {
        var outside = Directory.CreateDirectory(_workspace + "-outside").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(_workspace, ".agw"));
            Directory.CreateSymbolicLink(Path.Combine(_workspace, ".agw", "memory"), outside);
            var store = CreateStore();

            // 提供上下文时读取记忆索引就会被拒绝；直接写入同样被拒绝，外部目录保持为空。
            // Reading the memory index while providing context is already rejected; a direct write is rejected as
            // well, and the outside directory stays empty.
            var indexRead = await Assert.ThrowsAsync<AgwException>(() => InvokeProviderAsync(CreateProvider(store)));
            var write = await Assert.ThrowsAsync<AgwException>(() =>
                store.WriteAsync("notes.md", "escaped", TestContext.Current.CancellationToken)
            );

            Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, indexRead.Code);
            Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, write.Code);
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_MemoryFileSymlinkOutsideWorkspace_IsRejected()
    {
        var outside = Directory.CreateDirectory(_workspace + "-outside").FullName;
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(outside, "secret.txt"),
                "secret",
                TestContext.Current.CancellationToken
            );
            var memory = Directory.CreateDirectory(Path.Combine(_workspace, ".agw", "memory")).FullName;
            File.CreateSymbolicLink(Path.Combine(memory, "notes.md"), Path.Combine(outside, "secret.txt"));
            var context = await InvokeProviderAsync(CreateProvider(CreateStore()));
            var read = GetFunction(context, ProjectMemoryProvider.ReadFileToolName);

            var exception = await Assert.ThrowsAsync<AgwException>(async () =>
                await read.InvokeAsync(Arguments(("fileName", "notes.md")), TestContext.Current.CancellationToken)
            );

            Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_MemoryFileSymlinkInsideWorkspace_ReturnsContent()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "shared.md"),
            "shared",
            TestContext.Current.CancellationToken
        );
        var memory = Directory.CreateDirectory(Path.Combine(_workspace, ".agw", "memory")).FullName;
        File.CreateSymbolicLink(Path.Combine(memory, "link.md"), Path.Combine(_workspace, "shared.md"));
        var context = await InvokeProviderAsync(CreateProvider(CreateStore()));

        var result = await GetFunction(context, ProjectMemoryProvider.ReadFileToolName)
            .InvokeAsync(Arguments(("fileName", "link.md")), TestContext.Current.CancellationToken);

        Assert.Equal("shared", ResultText(result));
    }

    [Fact]
    public async Task WriteAsync_FileNameWithNulCharacter_IsRejected()
    {
        var context = await InvokeProviderAsync(CreateProvider(CreateStore()));
        var write = GetFunction(context, ProjectMemoryProvider.WriteToolName);

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await write.InvokeAsync(
                Arguments(("fileName", "notes\0.md"), ("content", "invalid")),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains("NUL", exception.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_workspace, ".agw")));
    }

    private ProjectAgentFileStore CreateStore() =>
        new(
            new LocalFileSystemResolver(new LocalFileSystem(_workspace)),
            Guid.CreateVersion7(),
            ProjectMemoryToolBlock.FileSystemRoot
        );

    private static ProjectMemoryProvider CreateProvider(AgwAgentFileStore store) =>
        new(store, InMemoryApplicationLock.Shared, "test-project-memory");

    private static async Task<AIContext> InvokeProviderAsync(ProjectMemoryProvider provider)
    {
        var agent = new ChatClientAgent(new StubChatClient(), new ChatClientAgentOptions { Name = "test-agent" });
        return await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(agent, null, new AIContext()),
            TestContext.Current.CancellationToken
        );
    }

    private static AIFunction GetFunction(AIContext context, string name)
    {
        var tools = context.Tools;
        Assert.NotNull(tools);
        return Assert.IsAssignableFrom<AIFunction>(Assert.Single(tools, tool => tool.Name == name));
    }

    private static AIFunctionArguments Arguments(params (string Name, object? Value)[] values) =>
        new(values.ToDictionary(static value => value.Name, static value => value.Value));

    private static string? ResultText(object? result) =>
        result is JsonElement element ? element.GetString() : Assert.IsType<string>(result);

    private static List<T> ResultList<T>(object? result)
    {
        var element = Assert.IsType<JsonElement>(result);
        return element.Deserialize<List<T>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
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

    private sealed class LocalFileSystemResolver : IAgwFileSystemResolver
    {
        private readonly IAgwFileSystem _fileSystem;

        public LocalFileSystemResolver(IAgwFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }

        public Task<IAgwFileSystem?> ResolveAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult<IAgwFileSystem?>(_fileSystem);
    }

    private sealed class RecordingEmptyAgentFileStore : AgwAgentFileStore
    {
        public int CreateDirectoryCallCount { get; private set; }

        public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public override Task<IReadOnlyList<AgwFileStoreEntry>> ListChildrenAsync(
            string directory,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<AgwFileStoreEntry>>([]);

        public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public override Task<IReadOnlyList<AgwFileSearchResult>> SearchAsync(
            string directory,
            string regexPattern,
            string? globPattern = null,
            bool recursive = false,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<AgwFileSearchResult>>([]);

        public override Task<int?> ReplaceTextAsync(
            string path,
            string oldString,
            string newString,
            bool replaceAll,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<int?>(null);

        public override Task<bool> ReplaceLinesAsync(
            string path,
            IReadOnlyList<AgwFileLineEdit> edits,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(false);

        public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            CreateDirectoryCallCount++;
            return Task.CompletedTask;
        }
    }
}
