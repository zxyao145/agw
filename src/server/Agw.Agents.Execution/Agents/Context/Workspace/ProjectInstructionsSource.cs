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
        var instructions = $"""
            # others

            - Your default workspace or working directory is '{workspace}'.
            """;
        if (context.Project.AdditionalDirectories.Count > 0)
        {
            instructions +=
                "\nAdditional Project directories (file mentions use @<absolutePath>, or @\"<absolute path>\" for spaces; when using relative-path file tools, match the absolute path to a listed directory and pass its directoryId with the path relative to that directory; omit directoryId for the primary directory):\n"
                + string.Join(
                    "\n",
                    context.Project.AdditionalDirectories.Select(directory =>
                        $"- directoryId={directory.Id:D}: {PathUtil.ExpandTilde(directory.Path)}"
                    )
                );
        }

        return ValueTask.FromResult<string?>(instructions);
    }
}
