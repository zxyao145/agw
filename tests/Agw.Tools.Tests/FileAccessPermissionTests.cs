using System.Runtime.CompilerServices;
using Agw.Files.Abstracts;
using Agw.Files.Application.Storage.Local;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Agw.Tools.Impl.ToolBlocks.FileAccess;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Tests;

public sealed class FileAccessPermissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokingAsync_ExistingContext_AddsOnlyFileToolsAndInstructions(bool additionalDirectory)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("agw-file-context-");
        try
        {
            const string instructions = "existing-agent-instructions";
            var message = new ChatMessage(ChatRole.User, "inspect workspace");
            var metadata = new AgwToolMetadata("built-in", AgwToolPermission.None);
            var existingTool = AgwToolMetadataBinding.Bind(
                AIFunctionFactory.Create((string before, string after) => $"{before}->{after}", "diff"),
                metadata
            );
            var projectId = Guid.CreateVersion7();
            var snapshot = ProjectWorkspacePaths.CreateSnapshot(
                projectId,
                root.FullName,
                additionalDirectory
                    ? [new ProjectWorkspaceDirectory(Guid.CreateVersion7(), root.CreateSubdirectory("extra").FullName)]
                    : []
            );
            var block = new FileAccessToolBlock(new FileSystemResolver(root.FullName));
            await using var contribution = await block.MaterializeAsync(
                new FileAccessToolBlockDefinition(),
                new ToolMaterializationContext
                {
                    Agent = new Agent(),
                    Project = new Project { Id = projectId, Workspace = root.FullName },
                    Workspace = root.FullName,
                    WorkspaceSnapshot = snapshot,
                    DefaultMode = "execute",
                },
                token
            );
            var provider = Assert.Single(contribution.ContextProviders);
            using var model = new FileToolModel("file_access_read");
            var agent = new ChatClientAgent(model);
            var session = await agent.CreateSessionAsync(token);

            // Act
            var result = await provider.InvokingAsync(
                new AIContextProvider.InvokingContext(
                    agent,
                    session,
                    new AIContext
                    {
                        Instructions = instructions,
                        Messages = [message],
                        Tools = [existingTool],
                    }
                ),
                token
            );

            // Assert
            Assert.Equal(1, result.Instructions!.Split(instructions, StringSplitOptions.None).Length - 1);
            Assert.Same(message, Assert.Single(result.Messages!));
            var preservedTool = Assert.IsAssignableFrom<AIFunction>(
                Assert.Single(result.Tools!, tool => tool.Name == existingTool.Name)
            );
            Assert.Same(existingTool, preservedTool);
            Assert.Equal(metadata, preservedTool.GetService<AgwToolMetadata>());
            Assert.False(preservedTool.JsonSchema.GetProperty("properties").TryGetProperty("directoryId", out _));
            var fileTools = result
                .Tools!.Where(tool => tool.Name != existingTool.Name)
                .Select(tool => Assert.IsAssignableFrom<AIFunction>(tool))
                .ToArray();
            Assert.Equal(block.Descriptor.MemberToolNames.Order(), fileTools.Select(tool => tool.Name).Order());
            Assert.All(
                fileTools,
                tool => Assert.True(tool.JsonSchema.GetProperty("properties").TryGetProperty("directoryId", out _))
            );
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("file_access_read")]
    [InlineData("file_access_write")]
    public async Task RunAsync_AdditionalDirectory_RoutesSdkToolUsingCapturedSnapshot(string toolName)
    {
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("agw-file-directories-");
        try
        {
            var primary = root.CreateSubdirectory("primary").FullName;
            var extra = root.CreateSubdirectory("extra").FullName;
            await File.WriteAllTextAsync(Path.Combine(primary, "README.md"), "primary content", token);
            await File.WriteAllTextAsync(Path.Combine(extra, "README.md"), "extra content", token);
            var projectId = Guid.CreateVersion7();
            var directoryId = Guid.CreateVersion7();
            var snapshot = ProjectWorkspacePaths.CreateSnapshot(
                projectId,
                primary,
                [new ProjectWorkspaceDirectory(directoryId, extra)]
            );
            await using var contribution = await new FileAccessToolBlock(
                new FileSystemResolver(primary)
            ).MaterializeAsync(
                new FileAccessToolBlockDefinition(),
                new ToolMaterializationContext
                {
                    Agent = new Agent(),
                    Project = new Project { Id = projectId, Workspace = primary },
                    Workspace = primary,
                    WorkspaceSnapshot = snapshot,
                    DefaultMode = "execute",
                },
                token
            );
            using var model = new FileToolModel(
                toolName,
                new Dictionary<string, object?>
                {
                    ["fileName"] = "README.md",
                    ["overwrite"] = true,
                    ["content"] = "updated extra",
                    ["directoryId"] = directoryId.ToString(),
                }
            );
            var agent = new ChatClientAgent(
                model,
                new ChatClientAgentOptions { AIContextProviders = contribution.ContextProviders }
            );
            var response = await agent.RunAsync(
                "use selected directory",
                await agent.CreateSessionAsync(token),
                cancellationToken: token
            );
            var result = Assert.Single(
                response.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            );
            Assert.Null(result.Exception);
            if (toolName == "file_access_read")
                Assert.Contains("extra content", System.Text.Json.JsonSerializer.Serialize(result.Result));
            else
                Assert.Equal("updated extra", await File.ReadAllTextAsync(Path.Combine(extra, "README.md"), token));
            Assert.Equal("primary content", await File.ReadAllTextAsync(Path.Combine(primary, "README.md"), token));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("file_access_ls", false)]
    [InlineData("file_access_read", false)]
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
                new ChatClientAgentOptions
                {
                    ChatOptions = new ChatOptions { Tools = [AIFunctionFactory.Create((string text) => text, "diff")] },
                    AIContextProviders = contribution.ContextProviders,
                }
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
                Assert.Contains(
                    toolName == "file_access_read" ? "hello" : "hello.txt",
                    System.Text.Json.JsonSerializer.Serialize(result.Result)
                );
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

    private sealed class FileToolModel : IChatClient
    {
        private readonly string _toolName;
        private bool _requested;
        private readonly Dictionary<string, object?>? _arguments;

        public FileToolModel(string toolName, Dictionary<string, object?>? arguments = null)
        {
            _toolName = toolName;
            _arguments = arguments;
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
                _arguments
                ?? (
                    _toolName switch
                    {
                        "file_access_ls" => new Dictionary<string, object?>(),
                        "file_access_read" => new Dictionary<string, object?> { ["fileName"] = "hello.txt" },
                        _ => new Dictionary<string, object?> { ["path"] = "new.txt", ["content"] = "new content" },
                    }
                );
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
