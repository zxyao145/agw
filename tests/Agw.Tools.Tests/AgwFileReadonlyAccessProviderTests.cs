using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Files.Abstracts;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Agw.Testing;
using Agw.Tools.Impl.ToolBlocks.FileAccess;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Tests;

public sealed class AgwFileReadonlyAccessProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        "file-readonly-access-provider",
        Guid.CreateVersion7().ToString("N")
    );
    private readonly string _primary;
    private readonly AgwFileAccessProvider _fileAccessProvider;
    private readonly AgwFileReadonlyAccessProvider _provider;

    public AgwFileReadonlyAccessProviderTests()
    {
        _primary = Directory.CreateDirectory(Path.Combine(_root, "primary")).FullName;
        var projectId = Guid.CreateVersion7();
        var snapshot = ProjectWorkspacePaths.CreateSnapshot(projectId, _primary);
        _fileAccessProvider = new AgwFileAccessProvider(new SnapshotFileSystemResolver(), projectId, snapshot);
        _provider = new AgwFileReadonlyAccessProvider(_fileAccessProvider);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task InvokingAsync_ExposesReadToolInstancesOfFileAccessProvider()
    {
        var context = await InvokeProviderAsync();

        var tools = context.Tools!.ToArray();
        Assert.Equal(
            [
                AgwFileAccessProvider.GrepToolName,
                AgwFileAccessProvider.LsToolName,
                AgwFileAccessProvider.ReadFileToolName,
                AgwFileAccessProvider.ReadLinesToolName,
            ],
            tools.Select(static tool => tool.Name).Order(StringComparer.Ordinal)
        );
        Assert.All(
            tools,
            tool =>
                Assert.Same(Assert.Single(_fileAccessProvider.Tools, candidate => candidate.Name == tool.Name), tool)
        );
        Assert.DoesNotContain(AgwFileAccessProvider.WriteToolName, context.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain(AgwFileAccessProvider.DeleteFileToolName, context.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_ExistingFile_ReturnsContent()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_primary, "notes.txt"), "readonly content", token);
        var context = await InvokeProviderAsync();
        var read = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(context.Tools!, tool => tool.Name == AgwFileAccessProvider.ReadFileToolName)
        );

        var result = await read.InvokeAsync(new AIFunctionArguments { ["fileName"] = "notes.txt" }, token);

        Assert.Equal("readonly content", Assert.IsType<JsonElement>(result).GetString());
    }

    [Fact]
    public async Task GrepAsync_DefaultPage_IsCompactAndCanContinue()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllLinesAsync(
            Path.Combine(_primary, "notes.txt"),
            Enumerable.Range(1, 250).Select(static line => $"needle {line}"),
            token
        );
        var grep = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(_fileAccessProvider.Tools, tool => tool.Name == AgwFileAccessProvider.GrepToolName)
        );

        var firstPage = Assert.IsType<JsonElement>(
            await grep.InvokeAsync(new AIFunctionArguments { ["regexPattern"] = "needle" }, token)
        );

        Assert.Equal(JsonValueKind.Object, firstPage.ValueKind);
        var firstMatches = ReadMatches(firstPage);
        Assert.Equal(20, firstMatches.Length);
        Assert.Equal(1, firstMatches[0].LineNumber);
        Assert.Equal("notes.txt", firstMatches[0].FileName);
        Assert.Equal("needle 1", firstMatches[0].Line);
        var firstFile = firstPage.GetProperty("files")[0];
        Assert.Equal("notes.txt", firstFile.GetProperty("fileName").GetString());
        Assert.False(firstFile.TryGetProperty("snippet", out _));

        var continuation = firstPage.GetProperty("continuation");
        Assert.Equal(JsonValueKind.Object, continuation.ValueKind);
        Assert.Equal("notes.txt", continuation.GetProperty("fileName").GetString());
        Assert.Equal(20, continuation.GetProperty("lineNumber").GetInt32());
        var secondPage = Assert.IsType<JsonElement>(
            await grep.InvokeAsync(
                new AIFunctionArguments { ["regexPattern"] = "needle", ["continuation"] = continuation },
                token
            )
        );
        var secondMatches = ReadMatches(secondPage);
        Assert.Equal(20, secondMatches.Length);
        Assert.Equal(21, secondMatches[0].LineNumber);
        Assert.Equal(40, secondMatches[^1].LineNumber);

        var matchingLines = new List<int>();
        var currentPage = firstPage;
        while (true)
        {
            matchingLines.AddRange(ReadMatches(currentPage).Select(static match => match.LineNumber));
            if (
                !currentPage.TryGetProperty("continuation", out var nextContinuation)
                || nextContinuation.ValueKind == JsonValueKind.Null
            )
            {
                break;
            }

            currentPage = Assert.IsType<JsonElement>(
                await grep.InvokeAsync(
                    new AIFunctionArguments { ["regexPattern"] = "needle", ["continuation"] = nextContinuation },
                    token
                )
            );
        }

        Assert.Equal(Enumerable.Range(1, 250), matchingLines);
    }

    [Fact]
    public async Task GrepAsync_LargeLinesAcrossFiles_ContinuationPassesResultCharacterBudget()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllLinesAsync(
            Path.Combine(_primary, "a-large-lines.txt"),
            Enumerable.Range(1, 10).Select(CreateLongMatchingLine),
            token
        );
        await File.WriteAllLinesAsync(
            Path.Combine(_primary, "b-large-lines.txt"),
            Enumerable.Range(1, 10).Select(CreateLongMatchingLine),
            token
        );
        var grep = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(_fileAccessProvider.Tools, tool => tool.Name == AgwFileAccessProvider.GrepToolName)
        );

        var firstPage = Assert.IsType<JsonElement>(
            await grep.InvokeAsync(new AIFunctionArguments { ["regexPattern"] = "needle" }, token)
        );
        var firstMatches = ReadMatches(firstPage);
        Assert.Equal(16, firstMatches.Length);
        Assert.Equal(4 * 1024, firstMatches[0].Line.Length);
        Assert.Equal(10, firstMatches.Count(static match => match.FileName == "a-large-lines.txt"));
        Assert.Equal(6, firstMatches.Count(static match => match.FileName == "b-large-lines.txt"));
        Assert.EndsWith("... [truncated]", firstMatches[^1].Line, StringComparison.Ordinal);

        var secondPage = Assert.IsType<JsonElement>(
            await grep.InvokeAsync(
                new AIFunctionArguments
                {
                    ["regexPattern"] = "needle",
                    ["continuation"] = firstPage.GetProperty("continuation"),
                },
                token
            )
        );
        var secondMatches = ReadMatches(secondPage);
        Assert.Equal("b-large-lines.txt", secondMatches[0].FileName);
        Assert.Equal(7, secondMatches[0].LineNumber);
    }

    [Fact]
    public async Task GrepAsync_ExactFinalPage_HasNoContinuation()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllLinesAsync(
            Path.Combine(_primary, "exact-page.txt"),
            Enumerable.Range(1, 40).Select(static line => $"needle {line}"),
            token
        );
        var grep = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(_fileAccessProvider.Tools, tool => tool.Name == AgwFileAccessProvider.GrepToolName)
        );

        var firstPage = Assert.IsType<JsonElement>(
            await grep.InvokeAsync(new AIFunctionArguments { ["regexPattern"] = "needle" }, token)
        );
        var secondPage = Assert.IsType<JsonElement>(
            await grep.InvokeAsync(
                new AIFunctionArguments
                {
                    ["regexPattern"] = "needle",
                    ["continuation"] = firstPage.GetProperty("continuation"),
                },
                token
            )
        );

        Assert.Equal(20, ReadMatches(secondPage).Length);
        Assert.False(secondPage.TryGetProperty("continuation", out _));
    }

    [Fact]
    public async Task GrepAsync_InvalidContinuation_IsRejected()
    {
        var token = TestContext.Current.CancellationToken;
        var grep = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(_fileAccessProvider.Tools, tool => tool.Name == AgwFileAccessProvider.GrepToolName)
        );

        await Assert.ThrowsAsync<Agw.Shared.Exceptions.AgwException>(async () =>
        {
            await grep.InvokeAsync(
                new AIFunctionArguments
                {
                    ["regexPattern"] = "needle",
                    ["continuation"] = JsonSerializer.SerializeToElement(
                        new { fileName = "notes.txt", lineNumber = 0 }
                    ),
                },
                token
            );
        });
    }

    [Fact]
    public async Task GrepAsync_MultipleFiles_OrdersMatchesByPathThenLine()
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllLinesAsync(Path.Combine(_primary, "z.txt"), ["needle z1", "needle z2"], token);
        await File.WriteAllLinesAsync(Path.Combine(_primary, "a.txt"), ["needle a1", "needle a2"], token);
        var grep = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(_fileAccessProvider.Tools, tool => tool.Name == AgwFileAccessProvider.GrepToolName)
        );

        var result = Assert.IsType<JsonElement>(
            await grep.InvokeAsync(new AIFunctionArguments { ["regexPattern"] = "needle" }, token)
        );
        var matches = ReadMatches(result);

        Assert.Equal(["a.txt", "a.txt", "z.txt", "z.txt"], matches.Select(static match => match.FileName));
        Assert.Equal([1, 2, 1, 2], matches.Select(static match => match.LineNumber));
    }

    private static (string FileName, int LineNumber, string Line)[] ReadMatches(JsonElement result) =>
        result
            .GetProperty("files")
            .EnumerateArray()
            .SelectMany(file =>
                file.GetProperty("matchingLines")
                    .EnumerateArray()
                    .Select(match =>
                        (
                            file.GetProperty("fileName").GetString()!,
                            match.GetProperty("lineNumber").GetInt32(),
                            match.GetProperty("line").GetString()!
                        )
                    )
            )
            .ToArray();

    private static string CreateLongMatchingLine(int lineNumber)
    {
        var prefix = $"needle {lineNumber} ";
        return string.Concat(prefix, new string('x', (4 * 1024) - prefix.Length));
    }

    private async Task<AIContext> InvokeProviderAsync()
    {
        var agent = new ChatClientAgent(new StubChatClient());
        return await _provider.InvokingAsync(
            new AIContextProvider.InvokingContext(agent, null, new AIContext()),
            TestContext.Current.CancellationToken
        );
    }

    private sealed class SnapshotFileSystemResolver : IAgwFileSystemResolver
    {
        public Task<IAgwFileSystem?> ResolveAsync(Guid projectId, CancellationToken ct) =>
            throw new InvalidOperationException("The file access provider must resolve from its captured snapshot.");

        public Task<IAgwFileSystem?> ResolveSnapshotAsync(
            Guid projectId,
            ProjectWorkspaceSnapshot snapshot,
            Guid? directoryId,
            CancellationToken ct
        ) => Task.FromResult<IAgwFileSystem?>(TestFileSystems.Local(snapshot.Workspace));
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
