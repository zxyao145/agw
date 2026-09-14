using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Configuration;

namespace Agw.Tools.Impl.ContextualTools.Shell;

[Description("Runs approved shell commands in the project workspace.")]
[DisplayName("Shell")]
[AgwToolRequiresWorkspace]
public sealed class ShellContextualTool : IContextualTool
{
    private readonly IConfiguration _configuration;

    public ShellContextualTool(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string Name => "run_shell";

    public string Category => "Shell";

    public AgwToolPermission RequiredPermission => AgwToolPermission.Execute;

    public ValueTask<ToolContribution> MaterializeAsync(
        ToolDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken
    )
    {
        if (definition is not RunShellToolDefinition)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Tool '{Name}' requires a {nameof(RunShellToolDefinition)}."
            );
        }

        var workspace = Path.GetFullPath(PathUtil.ExpandTilde(context.Workspace));
        var backend = _configuration["Agents:Shell:Backend"]?.Trim().ToLowerInvariant() ?? "local";
        ShellExecutor executor = backend switch
        {
            "local" => CreateLocalExecutor(context, workspace),
            "docker" => CreateDockerExecutor(context, workspace),
            _ => throw new AgwException(
                ErrorCodes.InvalidParam,
                "Agents:Shell:Backend must be either 'docker' or 'local'."
            ),
        };

        var contribution = new ToolContribution();
        contribution.Tools.Add(executor.AsAIFunction(requireApproval: false));
        contribution.ContextProviders.Add(new ShellEnvironmentProvider(executor));
        if (backend == "docker" && context.WorkspaceSnapshot?.AdditionalDirectories.Count > 0)
        {
            contribution.ContextProviders.Add(new DockerDirectoryProvider(context));
        }
        contribution.AddResource(executor);
        return ValueTask.FromResult(contribution);
    }

    private static LocalShellExecutor CreateLocalExecutor(ToolMaterializationContext context, string workspace)
    {
        var environment = context.EnvironmentVariables.ToDictionary(
            static pair => pair.Key,
            static pair => (string?)pair.Value,
            StringComparer.Ordinal
        );
        return new LocalShellExecutor(
            new LocalShellExecutorOptions
            {
                Mode = ShellMode.Persistent,
                WorkingDirectory = workspace,
                ConfineWorkingDirectory = true,
                CleanEnvironment = true,
                Environment = environment,
                Timeout = LocalShellExecutor.DefaultTimeout,
                // AgwToolMetadataBinding adds the approval wrapper from RequiredPermission.
                AcknowledgeUnsafe = true,
            }
        );
    }

    internal static IReadOnlyList<string> CreateAdditionalDirectoryMounts(ToolMaterializationContext context)
    {
        var args = new List<string>();
        var directories =
            context.WorkspaceSnapshot == null
                ? context.Project.AdditionalDirectories.Select(directory => (directory.Id, directory.Path))
                : context.WorkspaceSnapshot.AdditionalDirectories.Select(directory => (directory.Id, directory.Path));
        foreach (var (id, directoryPath) in directories)
        {
            var path = ProjectWorkspacePaths.Normalize(directoryPath);
            args.Add("--volume");
            args.Add($"{path}:/project-directories/{id:D}:rw");
        }
        return args;
    }

    private sealed class DockerDirectoryProvider : AIContextProvider
    {
        private readonly string _instructions;

        public DockerDirectoryProvider(ToolMaterializationContext context)
        {
            _instructions =
                "Docker shell starts in /workspace (the primary directory). Additional directories are mounted read/write at:\n"
                + string.Join(
                    "\n",
                    context.WorkspaceSnapshot!.AdditionalDirectories.Select(directory =>
                        $"- directoryId={directory.Id:D}: /project-directories/{directory.Id:D} (host: {directory.Path})"
                    )
                );
        }

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(new AIContext { Instructions = _instructions });
    }

    private static DockerShellExecutor CreateDockerExecutor(ToolMaterializationContext context, string workspace) =>
        new(
            new DockerShellExecutorOptions
            {
                Mode = ShellMode.Persistent,
                HostWorkdir = workspace,
                ContainerWorkdir = "/workspace",
                MountReadonly = false,
                ExtraRunArgs = CreateAdditionalDirectoryMounts(context),
                Network = DockerNetworkMode.None,
                Environment = context.EnvironmentVariables,
                Timeout = TimeSpan.FromSeconds(30),
            }
        );
}
