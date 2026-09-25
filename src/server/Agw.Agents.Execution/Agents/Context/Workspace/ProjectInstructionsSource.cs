using Agw.Shared.Utils;

namespace Agw.Agents.Execution.Agents.Context.Workspace;

internal sealed class ProjectInstructionsSource : IAgentInstructionsSource
{
    public ValueTask<string?> GetInstructionsAsync(
        AgwInstructionsSourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var configuredWorkspace = context.Project.Workspace;
        var workspace = PathUtil.ExpandTilde(
            string.IsNullOrEmpty(configuredWorkspace)
                ? ProjectDefaults.GetDefaultWorkspace(context.Project.Id)
                : configuredWorkspace
        );

        bool hasAdditionalDirectories = context.Project.AdditionalDirectories.Count > 0;
        string instructions = $"""
            # Project Workspace

            Your default workspace or working directory is '{workspace}', alias '{GetWorkspaceAlias(workspace)}'.
            User can use alias to refer to a specific directory, and user can also use 'default' to refer to the default workspace or working directory.
            """;
        if (hasAdditionalDirectories)
        {
            instructions +=
                "\nThe current project has the following additional directories (file mentions use @<absolutePath>, or @\"<absolute path>\" for spaces; when using relative-path file tools, match the absolute path to a listed directory and pass its directoryId with the path relative to that directory; omit directoryId for the primary directory):\n"
                + string.Join(
                    "\n",
                    context.Project.AdditionalDirectories.Select(directory =>
                        $"- directoryId={directory.Id:D}, alias={GetWorkspaceAlias(directory.Path)}: {PathUtil.ExpandTilde(directory.Path)}"
                    )
                );
        }

        return ValueTask.FromResult<string?>(instructions);
    }

    private static string GetWorkspaceAlias(string workspacePath)
    {
        return new DirectoryInfo(workspacePath).Name;
    }
}
