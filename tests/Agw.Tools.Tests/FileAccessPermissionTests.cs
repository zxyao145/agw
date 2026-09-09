using System.Runtime.CompilerServices;
using Agw.Files.Abstracts;
using Agw.Files.Application.Storage.Local;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Tools.Impl.ToolBlocks.FileAccess;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Tests;

public sealed class FileAccessPermissionTests
{
    [Theory]
    [InlineData("file_access_ls", false)]
    [InlineData("file_access_write", true)]
    public async Task RunAsync_RegistryFileTools_OnlyWritesRequireApproval(string toolName, bool requiresApproval)
    {
        var token = TestContext.Current.CancellationToken;
        var workspace = Directory.CreateTempSubdirectory("agw-file-permissions-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "hello.txt"), "hello", token);
            var registry = new ToolBlockRegistry([new FileAccessToolBlock(new FileSystemResolver(workspace))]);
            await using var contribution = await registry.MaterializeAsync(
                [new FileAccessToolBlockDefinition()],
                ToolBlockScope.Agent,
                new ToolMaterializationContext
                {
                    Agent = new Agent(),
                    Project = new Project { Id = Guid.CreateVersion7(), Workspace = workspace },
                    Workspace = workspace,
                    DefaultMode = "execute",
                },
                token
            );
            using var model = new FileToolModel(toolName);
            var agent = new ChatClientAgent(
                model,
                new ChatClientAgentOptions { AIContextProviders = contribution.ContextProviders }
            );
            var session = await agent.CreateSessionAsync(token);
            var response = await agent.RunAsync("inspect workspace", session, cancellationToken: token);
            var contents = response.Messages.SelectMany(static message => message.Contents).ToArray();
            if (requiresApproval)
            {
                Assert.Single(contents.OfType<ToolApprovalRequestContent>());
                Assert.Empty(contents.OfType<FunctionResultContent>());
                Assert.False(File.Exists(Path.Combine(workspace, "new.txt")));
            }
            else
            {
                Assert.Empty(contents.OfType<ToolApprovalRequestContent>());
                var result = Assert.Single(contents.OfType<FunctionResultContent>());
                Assert.Null(result.Exception);
                Assert.Contains("hello.txt", System.Text.Json.JsonSerializer.Serialize(result.Result));
            }
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class FileSystemResolver : IAgwFileSystemResolver
    {
        private readonly IAgwFileSystem _fileSystem;

        public FileSystemResolver(string workspace)
        {
            _fileSystem = new LocalFileSystem(workspace);
        }

        public Task<IAgwFileSystem?> ResolveAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult<IAgwFileSystem?>(_fileSystem);
    }

    private sealed class FileToolModel : IChatClient
    {
        private readonly string _toolName;
        private bool _requested;

        public FileToolModel(string toolName)
        {
            _toolName = toolName;
        }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            if (_requested)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
            _requested = true;
            var arguments =
                _toolName == "file_access_ls"
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?> { ["path"] = "new.txt", ["content"] = "new content" };
            return Task.FromResult(
                new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("file-call", _toolName, arguments)])
                )
            );
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }
    }
}
