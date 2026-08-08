using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;
using Agw.Tools.ToolBlocks.Blocks.ProjectMemory;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Tests;

public sealed class ProjectMemoryProviderTests
{
    private static readonly string[] ToolNames =
    [
        ProjectMemoryProvider.DeleteFileToolName,
        ProjectMemoryProvider.GrepToolName,
        ProjectMemoryProvider.LsToolName,
        ProjectMemoryProvider.ReadFileToolName,
        ProjectMemoryProvider.ReplaceToolName,
        ProjectMemoryProvider.ReplaceLinesToolName,
        ProjectMemoryProvider.WriteToolName
    ];

    [Fact]
    public async Task InvokingAsync_ExposesProjectToolsWithoutSessionState()
    {
        var provider = CreateProvider(new InMemoryAgentFileStore());

        var context = await InvokeProviderAsync(provider);
        var tools = context.Tools;
        Assert.NotNull(tools);

        Assert.Empty(provider.StateKeys);
        Assert.Contains("shared by all agents and conversations", context.Instructions);
        Assert.Equal(
            ToolNames,
            tools.Select(static tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.Null(context.Messages);
    }

    [Fact]
    public async Task WriteAsync_NewProviderInstanceReadsSharedMemoryAndIndex()
    {
        var store = new InMemoryAgentFileStore();
        var firstProvider = CreateProvider(store);
        var firstContext = await InvokeProviderAsync(firstProvider);
        var write = GetFunction(firstContext, ProjectMemoryProvider.WriteToolName);

        var writeResult = await write.InvokeAsync(
            Arguments(
                ("fileName", "architecture.md"),
                ("content", "Use a modular monolith."),
                ("description", "Architecture decisions")),
            TestContext.Current.CancellationToken);

        Assert.Equal("File 'architecture.md' written with description.", ResultText(writeResult));
        Assert.Equal(
            "Use a modular monolith.",
            await store.ReadAsync(
                "architecture.md",
                TestContext.Current.CancellationToken));
        var secondProvider = CreateProvider(store);
        var secondContext = await InvokeProviderAsync(secondProvider);
        var indexMessage = Assert.Single(secondContext.Messages!);
        Assert.Contains("architecture.md", indexMessage.Text);
        Assert.Contains("Architecture decisions", indexMessage.Text);

        var read = GetFunction(secondContext, ProjectMemoryProvider.ReadFileToolName);
        var readResult = await read.InvokeAsync(
            Arguments(("fileName", "architecture.md")),
            TestContext.Current.CancellationToken);
        Assert.Equal("Use a modular monolith.", ResultText(readResult));
    }

    [Fact]
    public async Task Tools_ListSearchReplaceLinesAndDeleteFollowMafBehavior()
    {
        var store = new InMemoryAgentFileStore();
        var context = await InvokeProviderAsync(CreateProvider(store));
        await GetFunction(context, ProjectMemoryProvider.WriteToolName).InvokeAsync(
            Arguments(
                ("fileName", "notes.md"),
                ("content", "alpha\nbeta\ngamma\n"),
                ("description", "Working notes")),
            TestContext.Current.CancellationToken);

        var listResult = ResultList<FileListEntry>(
            await GetFunction(context, ProjectMemoryProvider.LsToolName).InvokeAsync(
                Arguments(("globPattern", "*.md")),
                TestContext.Current.CancellationToken));
        var listedFile = Assert.Single(listResult);
        Assert.Equal("notes.md", listedFile.Name);
        Assert.Equal("Working notes", listedFile.Description);

        var searchResult = ResultList<FileSearchResult>(
            await GetFunction(context, ProjectMemoryProvider.GrepToolName).InvokeAsync(
                Arguments(("regexPattern", "BETA")),
                TestContext.Current.CancellationToken));
        Assert.Equal("notes.md", Assert.Single(searchResult).FileName);

        var replaceResult = await GetFunction(
                context,
                ProjectMemoryProvider.ReplaceToolName)
            .InvokeAsync(
                Arguments(
                    ("fileName", "notes.md"),
                    ("oldString", "beta"),
                    ("newString", "delta")),
                TestContext.Current.CancellationToken);
        Assert.Equal("Replaced 1 occurrence(s) in 'notes.md'.", ResultText(replaceResult));

        var replaceLinesResult = await GetFunction(
                context,
                ProjectMemoryProvider.ReplaceLinesToolName)
            .InvokeAsync(
                Arguments(
                    ("fileName", "notes.md"),
                    ("edits", new List<FileLineEdit>
                    {
                        new() { LineNumber = 1, NewLine = "first\n" },
                        new() { LineNumber = 3, NewLine = string.Empty }
                    })),
                TestContext.Current.CancellationToken);
        Assert.Equal("Replaced 2 line(s) in 'notes.md'.", ResultText(replaceLinesResult));
        Assert.Equal(
            "first\ndelta\n",
            await store.ReadAsync("notes.md", TestContext.Current.CancellationToken));

        var deleteResult = await GetFunction(context, ProjectMemoryProvider.DeleteFileToolName)
            .InvokeAsync(
                Arguments(("fileName", "notes.md")),
                TestContext.Current.CancellationToken);
        Assert.Equal("File 'notes.md' deleted.", ResultText(deleteResult));
        Assert.Null(await store.ReadAsync("notes.md", TestContext.Current.CancellationToken));
        Assert.Null(await store.ReadAsync(
            "notes_description.md",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentWrites_SeparateProvidersKeepCompleteSharedIndex()
    {
        var store = new InMemoryAgentFileStore();
        var firstContext = await InvokeProviderAsync(CreateProvider(store));
        var secondContext = await InvokeProviderAsync(CreateProvider(store));
        var cancellationToken = TestContext.Current.CancellationToken;

        await Task.WhenAll(
            GetFunction(firstContext, ProjectMemoryProvider.WriteToolName).InvokeAsync(
                Arguments(("fileName", "first.md"), ("content", "first")),
                cancellationToken).AsTask(),
            GetFunction(secondContext, ProjectMemoryProvider.WriteToolName).InvokeAsync(
                Arguments(("fileName", "second.md"), ("content", "second")),
                cancellationToken).AsTask());

        var index = await store.ReadAsync("memories.md", cancellationToken);
        Assert.Contains("first.md", index);
        Assert.Contains("second.md", index);
    }

    [Fact]
    public async Task WriteAsync_NestedOrInternalNameIsRejected()
    {
        var context = await InvokeProviderAsync(CreateProvider(new InMemoryAgentFileStore()));
        var write = GetFunction(context, ProjectMemoryProvider.WriteToolName);

        var nested = await Assert.ThrowsAsync<AgwException>(async () => await write.InvokeAsync(
            Arguments(("fileName", "folder/notes.md"), ("content", "invalid")),
            TestContext.Current.CancellationToken));
        Assert.Contains("flat names", nested.ToString(), StringComparison.OrdinalIgnoreCase);

        var internalFile = await Assert.ThrowsAsync<AgwException>(async () => await write.InvokeAsync(
            Arguments(("fileName", "memories.md"), ("content", "invalid")),
            TestContext.Current.CancellationToken));
        Assert.Contains("reserved", internalFile.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectMemoryProvider CreateProvider(AgentFileStore store) =>
        new(store, InMemoryApplicationLock.Shared, "test-project-memory");

    private static async Task<AIContext> InvokeProviderAsync(ProjectMemoryProvider provider)
    {
        var agent = new ChatClientAgent(
            new StubChatClient(),
            new ChatClientAgentOptions { Name = "test-agent" });
        return await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(agent, null, new AIContext()),
            TestContext.Current.CancellationToken);
    }

    private static AIFunction GetFunction(AIContext context, string name)
    {
        var tools = context.Tools;
        Assert.NotNull(tools);
        return Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(tools, tool => tool.Name == name));
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
        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(
                [new ChatMessage(ChatRole.Assistant, "done")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }
    }
}
