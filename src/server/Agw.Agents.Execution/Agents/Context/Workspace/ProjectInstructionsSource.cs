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
                "\nAdditional Project directories (file mentions use @<directoryId>:<relativePath>, or @\"<directoryId>:<relative path>\" for spaces; file tools accept their directoryId; relative paths without directoryId use the primary directory):\n"
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
