using System.Text.Json;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Tools.Impl.ContextualTools.Shell;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

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

    private static async Task<ToolContribution> MaterializeAsync(string workspace, CancellationToken ct)
    {
        var tool = new ShellContextualTool(new ConfigurationBuilder().Build());
        var project = new Project { Id = Guid.CreateVersion7(), Workspace = workspace };
        var context = new ToolMaterializationContext
        {
            Agent = new Agent { Id = Guid.CreateVersion7() },
            Project = project,
            Workspace = workspace,
            DefaultMode = "execute",
        };
        return await tool.MaterializeAsync(new RunShellToolDefinition(), context, ct);
    }

    private static string CreateWorkspace() =>
        Directory
            .CreateDirectory(
                Path.Combine(AppContext.BaseDirectory, "shell-contextual-tool", Guid.CreateVersion7().ToString("N"))
            )
            .FullName;
}
