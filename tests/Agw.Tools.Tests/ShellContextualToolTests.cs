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
using static Agw.Tools.Tests.ZshTestEnvironment;

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

    [Theory(Skip = RequiresZsh, SkipUnless = nameof(HasZsh), SkipType = typeof(ZshTestEnvironment))]
    [InlineData("$ZDOTDIR/tools")]
    [InlineData("~/tools")]
    [InlineData("../zdotdir/tools")]
    public async Task RunShell_LoginShellAddsDirectory_RunsCommandFromThatDirectory(string toolsPath)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace();
        try
        {
            var directory = CreateLoginShellDirectory(
                root,
                $"""
                export PATH="{toolsPath}:$PATH"
                """
            );
            var environment = new Dictionary<string, string>
            {
                ["SHELL"] = "/bin/zsh",
                ["HOME"] = directory,
                ["ZDOTDIR"] = directory,
            };
            await using var contribution = await MaterializeAsync(Path.Combine(root, "workspace"), ct, environment);
            var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(contribution.Tools));

            var result = await RunAsync(function, "agw-shell-probe", ct);
            var envResult = await RunAsync(function, "/usr/bin/env agw-shell-probe", ct);

            Assert.Contains("agw-shell-probe-ok", result, StringComparison.Ordinal);
            Assert.Contains("agw-shell-probe-ok", envResult, StringComparison.Ordinal);
            Assert.DoesNotContain("PATH", environment.Keys);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Skip = RequiresZsh, SkipUnless = nameof(HasZsh), SkipType = typeof(ZshTestEnvironment))]
    public async Task RunShell_DirectoriesMovedAfterLoginShellQuery_KeepsLoginShellPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace();
        try
        {
            var directory = CreateLoginShellDirectory(
                root,
                """
                echo q >> "$ZDOTDIR/queries"
                export PATH="$ZDOTDIR/tools:$PATH"
                """
            );
            var workspace = Path.Combine(root, "workspace");
            var environment = new Dictionary<string, string> { ["SHELL"] = "/bin/zsh", ["ZDOTDIR"] = directory };
            await using var contribution = await MaterializeAsync(workspace, ct, environment);
            var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(contribution.Tools));

            var before = await RunAsync(function, "agw-shell-probe", ct);
            Directory.Move(workspace, Path.Combine(root, "workspace-backup"));
            var after = await RunAsync(function, "agw-shell-probe", ct);

            Assert.Contains("agw-shell-probe-ok", before, StringComparison.Ordinal);
            Assert.Contains("agw-shell-probe-ok", after, StringComparison.Ordinal);
            Assert.Equal("q", Assert.Single(await File.ReadAllLinesAsync(Path.Combine(directory, "queries"), ct)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Skip = RequiresZsh, SkipUnless = nameof(HasZsh), SkipType = typeof(ZshTestEnvironment))]
    public async Task RunShell_EnvironmentVariablesContainPath_UsesThatPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace();
        try
        {
            var directory = CreateLoginShellDirectory(root, "exit 7");
            var environment = new Dictionary<string, string>
            {
                ["PATH"] = $"{directory}/tools:/usr/bin:/bin",
                ["SHELL"] = "/bin/zsh",
                ["ZDOTDIR"] = directory,
            };
            await using var contribution = await MaterializeAsync(Path.Combine(root, "workspace"), ct, environment);
            var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(contribution.Tools));

            var result = await RunAsync(function, "agw-shell-probe", ct);

            Assert.Contains("agw-shell-probe-ok", result, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Skip = RequiresZsh, SkipUnless = nameof(HasZsh), SkipType = typeof(ZshTestEnvironment))]
    public async Task Materialize_LoginShellQueryFails_ThrowsShellLoginPathUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateWorkspace();
        try
        {
            var directory = CreateLoginShellDirectory(root, "exit 7");
            var environment = new Dictionary<string, string> { ["SHELL"] = "/bin/zsh", ["ZDOTDIR"] = directory };
            var workspace = Path.Combine(root, "workspace");

            var exception = await Assert.ThrowsAsync<AgwException>(() => MaterializeAsync(workspace, ct, environment));

            Assert.Equal(ErrorCodes.ShellLoginPathUnavailable.Code, exception.Code);
            Assert.Contains("code 7", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateLocalEnvironmentAsync_UnsupportedLoginShell_LeavesPathUnset()
    {
        var context = new ToolMaterializationContext
        {
            Agent = new Agent { Id = Guid.CreateVersion7() },
            Project = new Project { Id = Guid.CreateVersion7() },
            Workspace = AppContext.BaseDirectory,
            DefaultMode = "execute",
            EnvironmentVariables = new Dictionary<string, string> { ["SHELL"] = "/usr/local/bin/fish" },
        };

        var environment = await ShellContextualTool.CreateLocalEnvironmentAsync(
            context,
            TestContext.Current.CancellationToken
        );

        Assert.DoesNotContain("PATH", environment.Keys);
        Assert.Equal("/usr/local/bin/fish", environment["SHELL"]);
    }

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

    private static Task<ToolContribution> MaterializeAsync(
        string workspace,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environmentVariables = null
    ) => MaterializeAsync(workspace, [], backend: "local", ct, environmentVariables);

    private static async Task<ToolContribution> MaterializeAsync(
        string workspace,
        IReadOnlyList<ProjectWorkspaceDirectory> additionalDirectories,
        string backend,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environmentVariables = null
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
            EnvironmentVariables = environmentVariables ?? HostPath(),
        };
        ToolContribution? contribution = null;
        try
        {
            contribution = await tool.MaterializeAsync(new RunShellToolDefinition(), context, ct);
            contribution.AddResource(workspaceServices);
            return contribution;
        }
        finally
        {
            if (contribution is null)
                await workspaceServices.DisposeAsync();
        }
    }

    private static Dictionary<string, string> HostPath() =>
        new() { ["PATH"] = Environment.GetEnvironmentVariable("PATH")! };

    private static string CreateLoginShellDirectory(string root, string zshrc)
    {
        var directory = Path.Combine(root, "zdotdir");
        var tools = Directory.CreateDirectory(Path.Combine(directory, "tools"));
        var probe = Path.Combine(tools.FullName, "agw-shell-probe");
        File.WriteAllText(Path.Combine(directory, ".zshrc"), zshrc.ReplaceLineEndings("\n"));
        File.WriteAllText(probe, "#!/bin/sh\nprintf 'agw-shell-probe-ok'\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return directory;
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
