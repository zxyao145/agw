using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Files.Abstracts;
using Agw.Files.Infrastructure.Storage;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
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
        ) => Task.FromResult<IAgwFileSystem?>(new LocalFileSystem(snapshot.Workspace));
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
