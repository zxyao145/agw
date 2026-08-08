using Agw.Files.Utils;
using Agw.Shared.Contracts.Tools;
using Agw.Shared.Exceptions;

using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Configuration;

namespace Agw.Tools.ContextualTools.Shell;

public sealed class ShellContextualTool : IContextualTool
{
    private readonly IConfiguration _configuration;

    public ShellContextualTool(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ToolInfo Descriptor { get; } = new()
    {
        Name = "run_shell",
        DisplayName = "Shell",
        Description = "Runs approved shell commands in the project workspace.",
        Category = "Shell",
        TypeName = typeof(ShellContextualTool).FullName!,
        Parameters = [],
        RequiresWorkspace = true,
        RequiresConfirmation = true
    };

    public ValueTask<ToolContribution> MaterializeAsync(
        ToolDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken)
    {
        if (definition is not RunShellToolDefinition)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Tool '{Descriptor.Name}' requires a {nameof(RunShellToolDefinition)}.");
        }

        var workspace = Path.GetFullPath(PathUtil.ExpandTilde(context.Workspace));
        var backend = _configuration["Agents:Shell:Backend"]?.Trim().ToLowerInvariant() ?? "local";
        ShellExecutor executor = backend switch
        {
            "local" => CreateLocalExecutor(context, workspace),
            "docker" => CreateDockerExecutor(context, workspace),
            _ => throw new AgwException(
                ErrorCodes.InvalidParam,
                "Agents:Shell:Backend must be either 'docker' or 'local'.")
        };

        var contribution = new ToolContribution();
        contribution.Tools.Add(executor.AsAIFunction(requireApproval: true));
        contribution.ContextProviders.Add(new ShellEnvironmentProvider(executor));
        contribution.AddResource(executor);
        return ValueTask.FromResult(contribution);
    }

    private static LocalShellExecutor CreateLocalExecutor(
        ToolMaterializationContext context,
        string workspace)
    {
        var environment = context.EnvironmentVariables.ToDictionary(
            static pair => pair.Key,
            static pair => (string?)pair.Value,
            StringComparer.Ordinal);
        return new LocalShellExecutor(new LocalShellExecutorOptions
        {
            Mode = ShellMode.Persistent,
            WorkingDirectory = workspace,
            ConfineWorkingDirectory = true,
            CleanEnvironment = true,
            Environment = environment,
            Timeout = LocalShellExecutor.DefaultTimeout
        });
    }

    private static DockerShellExecutor CreateDockerExecutor(
        ToolMaterializationContext context,
        string workspace) =>
        new(new DockerShellExecutorOptions
        {
            Mode = ShellMode.Persistent,
            HostWorkdir = workspace,
            ContainerWorkdir = "/workspace",
            MountReadonly = false,
            Network = DockerNetworkMode.None,
            Environment = context.EnvironmentVariables,
            Timeout = TimeSpan.FromSeconds(30)
        });
}
