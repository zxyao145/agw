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

    /// <summary>
    /// 交给模型的函数说明：shell 是优先级最低的工具，且不能用于文件操作。
    /// The function description shown to the model: the shell is the lowest-priority tool and cannot be used for file
    /// operations.
    /// </summary>
    internal const string ShellFunctionDescription =
        "Runs a shell command in the project workspace. This is the lowest-priority tool: use it only when no other "
        + "available tool can do the job, such as builds, tests, package managers and git. It cannot read, list, search, "
        + "write, edit or delete files; use the file_access_* tools for those.";

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
        // 文件访问约束在函数层与执行器策略两处生效：函数层给模型带错误码的说明，执行器策略拦住其他任何调用路径。
        // The file access restriction applies at both the function and the executor policy: the function gives the
        // model an explanation with an error code, and the executor policy stops every other invocation path.
        contribution.Tools.Add(
            new ShellFileAccessGuardAIFunction(
                executor.AsAIFunction(description: ShellFunctionDescription, requireApproval: false)
            )
        );
        contribution.ContextProviders.Add(new ShellFileAccessInstructionsProvider());
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
                Policy = ShellFileAccessPolicy.CreateShellPolicy(),
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
                Policy = ShellFileAccessPolicy.CreateShellPolicy(),
                Timeout = TimeSpan.FromSeconds(30),
            }
        );
}
