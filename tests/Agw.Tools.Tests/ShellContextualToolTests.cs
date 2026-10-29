using System.Text.Json;
using Agw.Files.Abstracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Agw.Tools.Impl.ContextualTools.Shell;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.Tests;

public sealed class ShellContextualToolTests
{
    [Fact]
    public async Task RunShell_FileAccessCommand_IsRejectedBeforeExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = CreateWorkspace();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "secret.txt"), "secret", ct);
            await using var contribution = await MaterializeAsync(workspace, ct);
            var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(contribution.Tools));

            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                function.InvokeAsync(new AIFunctionArguments { ["command"] = "cat secret.txt" }, ct).AsTask()
            );

            Assert.Equal(ErrorCodes.ShellFileAccessNotAllowed.Code, exception.Code);
            Assert.Contains("'cat'", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
            Assert.Contains(
                contribution.ContextProviders,
                static provider => provider is ShellFileAccessInstructionsProvider
            );
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task Materialize_TellsTheModelShellHasLowestPriority()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = CreateWorkspace();
        try
        {
            await using var contribution = await MaterializeAsync(workspace, ct);
            var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(contribution.Tools));

            Assert.Equal("run_shell", function.Name);
            Assert.Contains("lowest-priority tool", function.Description, StringComparison.Ordinal);
            Assert.Contains("file_access_*", function.Description, StringComparison.Ordinal);
            Assert.Contains(
                "lowest priority of all your tools",
                ShellFileAccessInstructionsProvider.Instructions,
                StringComparison.Ordinal
            );
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task RunShell_ProgramExecution_RunsInWorkspace()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = CreateWorkspace();
        try
        {
            await using var contribution = await MaterializeAsync(workspace, ct);
            var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(contribution.Tools));

            var result = await function.InvokeAsync(new AIFunctionArguments { ["command"] = "pwd" }, ct);

            Assert.Contains(Path.GetFileName(workspace), JsonSerializer.Serialize(result), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    public static bool UsesPosixShell => !OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(UsesPosixShell), Skip = "The commands use POSIX shell syntax.")]
    public async Task RunShell_DirectoriesMovedBetweenCalls_RecreatesDirectoriesAndRestartsShell()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var additional = Path.Combine(root, "additional");
            await using var contribution = await MaterializeAsync(
                workspace,
                [new ProjectWorkspaceDirectory(Guid.CreateVersion7(), additional)],
                backend: "local",
                ct
            );
            var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(contribution.Tools));
            Assert.True(Directory.Exists(workspace));
            Assert.True(Directory.Exists(additional));
            var before = await RunAsync(function, "export AGW_SHELL_MARK=bound; echo \"$AGW_SHELL_MARK\"; pwd -P", ct);
            Assert.Contains("bound", before, StringComparison.Ordinal);
            Assert.Contains(workspace, before, StringComparison.Ordinal);

            Directory.Move(workspace, Path.Combine(root, "workspace-backup"));
            Directory.Move(additional, Path.Combine(root, "additional-backup"));
            var after = await RunAsync(function, "echo \"${AGW_SHELL_MARK:-unset}\"; pwd -P", ct);

            Assert.Contains("unset", after, StringComparison.Ordinal);
            Assert.Contains(workspace, after, StringComparison.Ordinal);
            Assert.DoesNotContain("workspace-backup", after, StringComparison.Ordinal);
            Assert.True(Directory.Exists(workspace));
            Assert.True(Directory.Exists(additional));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static bool DockerAvailable =>
        !OperatingSystem.IsWindows() && DockerShellExecutor.IsAvailableAsync().GetAwaiter().GetResult();

    [Fact(SkipUnless = nameof(DockerAvailable), Skip = "Requires a docker command that runs Linux containers.")]
    public async Task RunShell_DockerDirectoriesRecreated_MountsCurrentDirectories()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var additional = Path.Combine(root, "additional");
            var directoryId = Guid.CreateVersion7();
            await using var contribution = await MaterializeAsync(
                workspace,
                [new ProjectWorkspaceDirectory(directoryId, additional)],
                backend: "docker",
                ct
            );
            var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(contribution.Tools));
            Assert.True(Directory.Exists(additional));
            await File.WriteAllTextAsync(Path.Combine(workspace, "old.txt"), "old", ct);
            await File.WriteAllTextAsync(Path.Combine(additional, "old.txt"), "old", ct);
            var mounted = await RunAsync(
                function,
                $"test -f /workspace/old.txt && test -f /project-directories/{directoryId:D}/old.txt && echo mounted-old",
                ct
            );
            Assert.Contains("mounted-old", mounted, StringComparison.Ordinal);

            Directory.Move(workspace, Path.Combine(root, "workspace-backup"));
            Directory.Move(additional, Path.Combine(root, "additional-backup"));
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(additional);
            await File.WriteAllTextAsync(Path.Combine(workspace, "new.txt"), "new", ct);
            await File.WriteAllTextAsync(Path.Combine(additional, "new.txt"), "new", ct);
            var current = await RunAsync(
                function,
                $"test -f /workspace/new.txt && test -f /project-directories/{directoryId:D}/new.txt && echo mounted-new; "
                    + $"test -e /workspace/old.txt || test -e /project-directories/{directoryId:D}/old.txt || echo old-hidden",
                ct
            );

            Assert.Contains("mounted-new", current, StringComparison.Ordinal);
            Assert.Contains("old-hidden", current, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> RunAsync(AIFunction function, string command, CancellationToken ct) =>
        JsonSerializer.Serialize(await function.InvokeAsync(new AIFunctionArguments { ["command"] = command }, ct));

    private static Task<ToolContribution> MaterializeAsync(string workspace, CancellationToken ct) =>
        MaterializeAsync(workspace, [], backend: "local", ct);

    private static async Task<ToolContribution> MaterializeAsync(
        string workspace,
        IReadOnlyList<ProjectWorkspaceDirectory> additionalDirectories,
        string backend,
        CancellationToken ct
    )
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Agents:Shell:Backend"] = backend })
            .Build();
        var workspaceServices = ProjectWorkspaceAccessTestServices.Create();
        var tool = new ShellContextualTool(
            configuration,
            workspaceServices.GetRequiredService<IProjectWorkspaceAccess>()
        );
        var projectId = Guid.CreateVersion7();
        var project = new Project { Id = projectId, Workspace = workspace };
        var context = new ToolMaterializationContext
        {
            Agent = new Agent { Id = Guid.CreateVersion7() },
            Project = project,
            Workspace = workspace,
            WorkspaceSnapshot = ProjectWorkspacePaths.CreateSnapshot(projectId, workspace, additionalDirectories),
            DefaultMode = "execute",
        };
        var contribution = await tool.MaterializeAsync(new RunShellToolDefinition(), context, ct);
        contribution.AddResource(workspaceServices);
        return contribution;
    }

    private static string CreateWorkspace() =>
        Directory
            .CreateDirectory(
                Path.Combine(
                    AppContext.BaseDirectory,
                    ".scratch",
                    "shell-contextual-tool",
                    Guid.CreateVersion7().ToString("N")
                )
            )
            .FullName;
}
